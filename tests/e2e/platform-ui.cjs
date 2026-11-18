const assert = require('node:assert/strict');
const { mkdir, writeFile } = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const { createPreviewServer } = require('../../scripts/mock-api/platform-preview.cjs');

(async () => {
  const server = createPreviewServer();
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({ headless: true, channel: process.env.PLAYWRIGHT_CHANNEL || undefined, args: ['--no-sandbox'] });
    const context = await browser.newContext({ viewport: { width: 1366, height: 900 }, serviceWorkers: 'block' });
    await context.addInitScript(() => localStorage.setItem('pos.lang', 'en'));
    const page = await context.newPage();
    // Every save asks first (owner, 2026-10-10): answers the question when one comes up.
    const answerIfAsked = async () => {
      const ok = page.locator('app-confirm-dialog .btn-primary');
      if (await ok.waitFor({ timeout: 3000 }).then(() => true, () => false)) await ok.click();
    };
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    const output = path.resolve(__dirname, '../../artifacts/platform-ui');
    await mkdir(output, { recursive: true });
    await page.goto(`http://127.0.0.1:${server.address().port}/#/platform`);
    await page.getByRole('heading', { name: 'Company subscriptions' }).waitFor();
    await page.getByRole('button', { name: /alger-centre/ }).click();
    const reason = page.locator('input[name=reason]');
    const save = page.getByRole('button', { name: 'Save subscription', exact: true });
    await page.locator('input[name=plan]').waitFor();
    assert.equal(await save.isDisabled(), true, 'a reason is required');
    await page.locator('input[name=plan]').fill('Annual plan');
    await reason.fill('Renewal received');
    await save.click();
    await answerIfAsked();
    await page.getByRole('status').filter({ hasText: 'Changes saved.' }).waitFor();
    assert.equal(await page.locator('input[name=plan]').inputValue(), 'Annual plan');
    await page.locator('.history').getByText('Renewal received', { exact: true }).waitFor();
    // Upload a language, then enable it in the company's country settings.
    await mkdir(output, { recursive: true });
    const packFile = path.join(output, 'tzm.json');
    await writeFile(packFile, JSON.stringify({ code: 'tzm', label: 'Tamaziɣt', rtl: false, fallbackLanguage: 'fr', strings: { 'login.title': 'Tuqqna' } }));
    await page.locator('.languages input[type=file]').setInputFiles(packFile);
    await page.getByText('Ready to save: Tamaziɣt (tzm), 1 texts.').waitFor();
    await page.locator('input[name=packReason]').fill('New market');
    await page.getByRole('button', { name: 'Save language pack', exact: true }).click();
    await answerIfAsked();
    await page.locator('.languages td', { hasText: 'Tamaziɣt' }).waitFor();

    await page.locator('select[name=preset]').selectOption('TN');
    await page.getByRole('button', { name: 'Load preset', exact: true }).click();
    await page.getByText('Changing the currency does not convert').waitFor();
    await page.getByRole('checkbox', { name: 'Tamaziɣt' }).check();
    await page.getByLabel('Label (Tamaziɣt)').first().waitFor();
    assert.equal(await page.locator('input[name=currency]').inputValue(), 'TND');
    await page.locator('input[name=rounding]').fill('0.1');
    await page.locator('select[name=roundingMode]').selectOption('Up');
    const taxLabel = page.locator('.rate-row', { hasText: '7%' }).getByLabel('Français');
    await taxLabel.fill('Taux réduit');
    await page.locator('.sections li', { hasText: 'Legal text' }).getByRole('button', { name: 'Move up' }).click();
    await page.locator('.sections li', { hasText: 'Footer message' }).getByRole('checkbox').uncheck();
    await page.getByLabel('Legal text on receipts (Français)').fill('Mention légale');
    await page.locator('input[name=regionalReason]').fill('Company moved to Tunisia');
    await page.getByRole('button', { name: 'Save country settings', exact: true }).click();
    await answerIfAsked();
    await page.locator('.history').getByText('Company moved to Tunisia', { exact: true }).waitFor();
    assert.equal(await page.locator('input[name=decimals]').inputValue(), '3', 'saved profile is reloaded');
    assert.equal(await page.locator('input[name=rounding]').inputValue(), '0.1');
    assert.equal(await page.locator('.rate-row', { hasText: '7%' }).getByLabel('Français').inputValue(), 'Taux réduit');
    assert.equal(await page.getByLabel('Legal text on receipts (Français)').inputValue(), 'Mention légale');
    const order = await page.locator('.sections li').allTextContents();
    assert.ok(order.findIndex(t => t.includes('Legal text')) < order.findIndex(t => t.includes('Payment and change')), 'legal moved up');
    assert.equal(await page.locator('.sections li', { hasText: 'Footer message' }).getByRole('checkbox').isChecked(), false);
    await page.screenshot({ path: path.join(output, 'desktop-en.png'), fullPage: true });

    // Create a company: the one-time password is shown once, then gone.
    await page.getByRole('button', { name: 'New company', exact: true }).click();
    const create = page.getByRole('button', { name: 'Create company', exact: true });
    assert.equal(await create.isDisabled(), true, 'required fields first');
    await page.locator('input[name=companyId]').fill('tlemcen-shop');
    await page.locator('textarea[name=hosts]').fill('tlemcen.pos.example.com');
    await page.locator('select[name=country]').selectOption('TN');
    await page.locator('input[name=createReason]').fill('Signed contract');
    await create.click();
    await answerIfAsked();
    const secret = page.getByTestId('one-time-password');
    await secret.waitFor();
    assert.equal(await secret.textContent(), 'Preview-one-time-01');
    await page.screenshot({ path: path.join(output, 'create-company.png'), fullPage: true });
    await page.getByRole('button', { name: 'Done', exact: true }).click();
    await page.getByRole('heading', { name: 'tlemcen-shop' }).waitFor();
    assert.equal(await secret.count(), 0, 'password is gone after Done');
    await page.getByText('Created in the portal').first().waitFor();
    await page.locator('input[name=hostingReason]').fill('Trial ended');
    // Archiving asks in the app's own dialog since 2026-10-05, not the browser's confirm box.
    await page.getByRole('button', { name: 'Archive company', exact: true }).click();
    await page.locator('app-confirm-dialog .btn-danger-solid, app-confirm-dialog .btn-primary').first().click();
    await page.getByRole('button', { name: /tlemcen-shop/ }).getByText('Archived').waitFor();
    await page.getByRole('button', { name: 'Restore company', exact: true }).waitFor();
    await page.getByRole('button', { name: /alger-centre/ }).click();
    await page.getByText('This company is defined in the server configuration.').waitFor();

    await page.getByLabel('Find a company').fill('oran');
    assert.equal(await page.locator('.company').count(), 1);
    await page.getByLabel('Find a company').fill('');
    await page.getByLabel('Language', { exact: true }).selectOption('ar');
    // A language change asks first, then switches after a five-second countdown (owner, 2026-10-12).
    await answerIfAsked();
    await page.getByRole('heading', { name: 'اشتراكات الشركات', exact: true }).waitFor();
    assert.equal(await page.locator('html').getAttribute('dir'), 'rtl');
    await page.screenshot({ path: path.join(output, 'desktop-ar.png'), fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({ path: path.join(output, 'mobile-ar.png'), fullPage: true });
    const layout = await page.evaluate(() => ({ width: window.innerWidth, scroll: document.documentElement.scrollWidth }));
    assert.ok(layout.scroll <= layout.width, `mobile overflow: ${JSON.stringify(layout)}`);
    assert.deepEqual(errors, []);
    console.log('PASS: portal selection, save/audit, country profile preset/save, rounding, rate labels, receipt layout, language pack upload, company creation/archive, search, Arabic RTL, mobile layout. Screenshots: artifacts/platform-ui');
  } finally {
    if (browser) await browser.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
