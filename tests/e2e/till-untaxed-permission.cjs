const { requiredPassword } = require('./test-credentials.cjs');
requiredPassword();
// "Sell untaxed at the till" (sales.untaxed; TAX-01, finding E#1; owner, 2026-11-02). A cashier whose sign-in
// says they may not starts every sale taxed: the box is ticked, locked and said in words beside it, and stays so
// after each sale; the sale reaches the server taxed. A cashier who may keeps today's untaxed start. Runs the
// built PWA against the mock API twice, once for each answer. Screenshots (fr, ar) go to SHOTS when set.
//
//   ADMIN_PASSWORD=... node tests/e2e/till-untaxed-permission.cjs   (after `npm run build` in ParadisERP.WebUI)
const { spawn } = require('node:child_process');
const path = require('node:path');
const { chromium } = require('playwright');

const PORT = Number(process.env.MOCK_PORT || 5096);
const BASE = `http://localhost:${PORT}`;
const SHOTS = process.env.SHOTS || null;

let failures = 0;
const check = (name, ok, extra = '') => {
  console.log(`${ok ? '✅' : '❌'} ${name}${extra ? ' — ' + extra : ''}`);
  if (!ok) failures++;
};

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

async function run(label, capabilities, expectLocked) {
  const mock = spawn(process.execPath, [path.resolve(__dirname, '../../scripts/mock-api/server.js')], {
    env: { ...process.env, MOCK_PORT: String(PORT), MOCK_CASHIER_CAPABILITIES: JSON.stringify(capabilities) },
    stdio: 'ignore',
  });
  let browser;
  try {
    const up = await waitFor(async () => (await fetch(`${BASE}/__state`)).ok);
    if (!up) throw new Error(`mock api never came up on ${BASE}`);
    browser = await chromium.launch({ headless: true, channel: 'chrome' });

    for (const lang of SHOTS ? ['fr', 'ar'] : ['fr']) {
      const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
      await context.addInitScript((l) => localStorage.setItem('pos.lang', l), lang);
      const page = await context.newPage();
      const errors = [];
      page.on('pageerror', (e) => errors.push(e.message));

      await page.goto(BASE, { waitUntil: 'networkidle' });
      await page.fill('#server', BASE);
      await page.fill('#username', 'caisse1');
      await page.fill('#password', 'Caisse@123');
      await page.click('button[type=submit]');
      await page.waitForSelector('.tile', { timeout: 30000 });
      await page.locator('.tile .tile-add').first().click();

      const box = page.locator('#taxmode');
      const state = async () => [await box.isChecked(), await box.isDisabled(), await page.locator('#taxmode-locked').count()];
      if (expectLocked) {
        check(`${label} ${lang}: the sale starts taxed, the box locked and said in words`,
          JSON.stringify(await state()) === JSON.stringify([true, true, 1]), JSON.stringify(await state()));
      } else {
        check(`${label} ${lang}: the sale starts untaxed and the box can be ticked`,
          JSON.stringify(await state()) === JSON.stringify([false, false, 0]), JSON.stringify(await state()));
      }
      if (SHOTS) await page.screenshot({ path: path.join(SHOTS, `${label}-${lang}-1440.png`) });

      if (lang === 'fr') {
        await page.click('button.pay');
        await page.waitForSelector('.modal');
        await page.fill('#received', '100000');
        await page.click('.actions .btn-primary');
        await page.waitForSelector('.done', { timeout: 10000 });
        const sold = await waitFor(async () => {
          const s = await (await fetch(`${BASE}/__state`)).json();
          return s.sales.length ? s.sales[s.sales.length - 1] : null;
        }, 20000);
        check(`${label}: the sale reaches the server ${expectLocked ? 'taxed' : 'untaxed'}`,
          !!sold && sold.taxMode === (expectLocked ? 'Taxed' : 'Untaxed'), sold ? sold.taxMode : 'never arrived');

        // The next customer: the same start again.
        await page.keyboard.press('Escape');
        await page.locator('.tile .tile-add').first().click();
        check(`${label}: the next sale starts the same way`,
          JSON.stringify(await state()) === JSON.stringify(expectLocked ? [true, true, 1] : [false, false, 0]), JSON.stringify(await state()));
      }
      check(`${label} ${lang}: no page errors`, errors.length === 0, errors.join(' | '));
      await context.close();
    }
  } catch (error) {
    check(`${label}: run completed`, false, error.message);
  } finally {
    if (browser) await browser.close();
    mock.kill();
    await new Promise((r) => setTimeout(r, 500));
  }
}

(async () => {
  await run('without', [], true);
  await run('with', ['sales.untaxed'], false);
  console.log(failures ? `\n${failures} CHECK(S) FAILED` : '\nALL CHECKS PASSED');
  process.exit(failures ? 1 : 0);
})();

