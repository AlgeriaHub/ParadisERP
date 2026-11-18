const { requiredPassword } = require('./test-credentials.cjs');
// FR-1 (the owner, 2026-10-03): "if the customer is new, create them from the screen itself" —
// from the rental contract form and from the till, through the existing customer editor.
//
// Drives the real screens against a live API, because what is checked is the whole loop: the
// server creates the customer, the till pulls it into its copy, and the screen picks it while the
// contract being typed or the cart being rung up stays exactly as it was.
//
//   1. the login of an Admin and of a Manager reports customers.create and the server accepts their
//      POST; a Cashier's login does not and the server refuses it (403) — no new right is granted;
//   2. an Admin / Manager online sees "+ New customer"; a Cashier does not;
//   3. an authorised user offline does not (no offline creation); nor does an old session whose
//      stored login predates capabilities — and that session raises no error;
//   4. creating one selects it, on the rental form and on the till;
//   5. opening, cancelling or completing the editor leaves the rental form / the till as it was —
//      the same component instance (marked before, still marked after) with every value unchanged;
//   6. no code is typed and a person is asked no tax number (owner, 2026-10-05): the server draws
//      the code, and every customer this run makes answers with one of the person series (Cust-…).
//
// Start the API against a local database and run:
//   BASE=http://127.0.0.1:5199 node tests/e2e/customer-quick-create.cjs
// Accounts: POS_ADMIN / POS_ADMIN_PASSWORD (default admin / [runtime test password]). A branch manager
// POS_MANAGER / POS_MANAGER_PASSWORD and a cashier POS_CASHIER / POS_CASHIER_PASSWORD may be given;
// when they are not, the run creates a throw-away Manager and Cashier at the terminal's branch and
// deletes them at the end. POS_DEVICE is the till code everyone signs in on (default C01).
// The customers it creates are named "E2E FR-1 …" and deleted again at the end.
const { chromium } = require('playwright');

const BASE = process.env.BASE || 'http://127.0.0.1:5199';
const ADMIN = [process.env.POS_ADMIN || 'admin', requiredPassword('POS_ADMIN_PASSWORD')];
let MANAGER = process.env.POS_MANAGER ? [process.env.POS_MANAGER, process.env.POS_MANAGER_PASSWORD] : null;
let CASHIER = process.env.POS_CASHIER ? [process.env.POS_CASHIER, process.env.POS_CASHIER_PASSWORD] : null;
const CAPABILITY = 'customers.create';
const DEVICE = process.env.POS_DEVICE || 'C01';

let failures = 0;
const check = (name, ok, extra = '') => {
  console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${extra ? ' — ' + extra : ''}`);
  if (!ok) failures++;
};
const skip = (name, why) => console.log(`SKIP ${name} — ${why}`);
const stamp = Date.now().toString(36).toUpperCase();
const created = [];
const tempUsers = [];

/** The login answer, straight from the API: what the server says this user may do. */
async function apiLogin([username, password]) {
  const r = await fetch(BASE + '/api/auth/login', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ username, password, deviceCode: DEVICE }),
  });
  if (!r.ok) throw new Error(`login ${username}: ${r.status}`);
  return r.json();
}

const capabilitiesOf = (login) => login.user.capabilities ?? [];

// The server draws a customer's code (owner, 2026-10-05): Cust-, a location's three digits, YYMM, a
// sequence — Cust-3612610001 for a person. The month and the number are not asserted — the run may
// be on any date, on a database that holds customers. A new hire's number is drawn the same way.
const PERSON_CODE = /^Cust-[1-9]\d{2}\d{4}\d{3,}$/;
const EMPLOYEE_NUMBER = /^Emp-[1-9]\d{2}\d{4}\d{3,}$/;

/**
 * POSTs a person as this login and returns the status and the code the server drew (null when it
 * was refused); one that was created is deleted again. No code is sent: the server draws it.
 */
async function tryCreate(login, adminToken, label) {
  const r = await fetch(BASE + '/api/catalog/customers', {
    method: 'POST',
    headers: { 'content-type': 'application/json', Authorization: `Bearer ${login.token}` },
    body: JSON.stringify({ fullName: `E2E FR-1 API ${label} ${stamp}`, creditLimit: 0, kind: 'Person' }),
  });
  let code = null;
  if (r.status === 201) {
    const c = await r.json();
    code = c.code ?? null;
    await fetch(`${BASE}/api/catalog/customers/${c.id}`, { method: 'DELETE', headers: { Authorization: `Bearer ${adminToken}` } });
  }
  return { status: r.status, code };
}

/** A throw-away user at the terminal's branch, deleted at the end of the run. */
// An account is given to an employee since 2026-10-05: the employee first, then the account, which
// signs in with the Login ID the server drew. The employee stays (there is no delete in Staff).
async function tempUser(adminLogin, role) {
  const password = `E2e-${stamp}-pw`;
  const headers = { 'content-type': 'application/json', Authorization: `Bearer ${adminLogin.token}` };
  const branchId = adminLogin.deviceBranchId ?? adminLogin.user.branchId;
  const hired = await fetch(BASE + '/api/hr/employees', {
    method: 'POST', headers,
    body: JSON.stringify({
      fullName: `E2E FR-1 ${role} ${stamp}`, branchId, payBasis: 'Monthly', baseRate: 30000, payoutMethod: 'Cash',
      hiredOn: '2026-01-01', isActive: true,
    }),
  });
  if (!hired.ok) throw new Error(`employee ${role}: ${hired.status} ${await hired.text()}`);
  const employee = await hired.json();
  check(`server: the ${role}'s employee is given a number the server drew`, EMPLOYEE_NUMBER.test(employee.number ?? ''), String(employee.number));
  const r = await fetch(BASE + '/api/users', {
    method: 'POST', headers,
    body: JSON.stringify({ employeeId: employee.id, role, password, pin: null, permissions: [], branchId }),
  });
  if (!r.ok) throw new Error(`create ${role}: ${r.status} ${await r.text()}`);
  const account = await r.json();
  tempUsers.push(account.id);
  return [account.loginId, password];
}

async function signIn(browser, [user, password], lang = 'fr', device = DEVICE) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, serviceWorkers: 'block' });
  await context.addInitScript((l) => localStorage.setItem('pos.lang', l), lang);
  const page = await context.newPage();
  page.on('pageerror', (e) => check(`no page error (${user})`, false, e.message));
  await page.goto(BASE + '/#/login', { waitUntil: 'networkidle' });
  await page.fill('#server', BASE);
  await page.fill('#username', user);
  await page.fill('#password', password);
  // A shared till's code sits behind the "shared till?" toggle since 2026-10-05.
  if (await page.locator('#device').count() === 0 && await page.locator('.shared-toggle').count()) await page.click('.shared-toggle');
  if (await page.locator('#device').count()) await page.fill('#device', device);
  await page.click('button[type=submit]');
  await page.waitForURL(/#\/(register|backoffice)/, { timeout: 20000 });
  const token = await page.evaluate(async () => {
    // The session token the API service sends, read the way the app reads it.
    const open = indexedDB.open('algeria-pos');
    const db = await new Promise((res, rej) => { open.onsuccess = () => res(open.result); open.onerror = rej; });
    const tx = db.transaction('meta', 'readonly');
    const all = await new Promise((res) => { const r = tx.objectStore('meta').getAll(); r.onsuccess = () => res(r.result); });
    const meta = all.find((m) => m && m.token) || all[0] || {};
    return meta.token || null;
  });
  return { context, page, token };
}

/**
 * Marks the screen's root element so a later look can tell whether it is the very same instance:
 * a parent re-created behind the dialog would come back unmarked, even with the same values typed.
 */
const mark = (page, selector) => page.$eval(selector, (el) => { el.__fr1 = 'kept'; });
const stillSame = (page, selector) => page.$eval(selector, (el) => el.__fr1 === 'kept');

/** What a rental form holds that a reader may have typed before opening the editor. */
const rentalState = (page) =>
  page.evaluate(() => ({
    notes: document.querySelector('#rf-notes')?.value,
    signed: document.querySelector('#rf-signed')?.value,
    branch: document.querySelector('#rf-branch')?.value ?? document.querySelector('#rf-fixed')?.textContent,
    lines: document.querySelectorAll('.rental-line').length,
  }));

/** What a till holds that the cashier may have set before opening the editor. */
const tillState = (page) =>
  page.evaluate(() => ({
    lines: [...document.querySelectorAll('.cart .line')].map((l) => [l.querySelector('.line-name')?.textContent?.trim(), l.querySelector('.stepper input')?.value]),
    discountType: document.querySelector('#disc-type')?.value,
    discountValue: document.querySelector('#disc-value')?.value,
    taxed: document.querySelector('#taxmode')?.checked,
    total: document.querySelector('.cart-pay .grand dd')?.textContent?.trim(),
  }));

/**
 * Fills the customer editor with a valid new person and saves it; returns the name used. No code
 * is typed — the server draws it — and no tax number: a natural person has none (2026-10-05).
 */
async function fillAndSave(page, label) {
  const name = `E2E FR-1 ${label} ${stamp}`;
  const modal = page.locator('.bo-edit-modal');
  // A new customer opens on the kind question alone since 2026-10-05: the large Person choice.
  await modal.locator('#cu-pick-person').click();
  // Waited for, not counted: a new row starts with no kind, which keeps the field until Person renders.
  const noTaxField = await modal.locator('#cu-tax').waitFor({ state: 'detached', timeout: 3000 }).then(() => true, () => false);
  check(`${label}: the editor asks a person for no tax number`, noTaxField);
  // A person's name in two inputs (owner, 2026-10-06); composed first name first, it is `name` again.
  const [first, ...family] = name.split(' ');
  await modal.locator('#cu-first-name').fill(first);
  await modal.locator('#cu-family-name').fill(family.join(' '));
  // Whatever the company has made mandatory, filled with something valid.
  for (const [sel, value] of [['#cu-phone', '0555000000'], ['#cu-email', 'e2e@example.com'], ['#cu-address', 'Rue E2E'], ['#cu-birth', '1990-01-01']]) {
    if (await modal.locator(sel).count()) await modal.locator(sel).fill(value);
  }
  await modal.locator('.bo-panel__foot .btn-primary').click();
  await page.locator('app-confirm-dialog .btn-primary').click(); // every save asks first (owner, 2026-10-10)
  await page.waitForSelector('.bo-edit-modal', { state: 'detached', timeout: 15000 });
  return name;
}

(async () => {
  const browser = await chromium.launch(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {});

  // ---------- the server's decision: login and route ----------
  const adminLogin = await apiLogin(ADMIN);
  const adminToken = adminLogin.token;
  if (!MANAGER) MANAGER = await tempUser(adminLogin, 'Manager');
  if (!CASHIER) CASHIER = await tempUser(adminLogin, 'Cashier');
  {
    check("server: an Admin's login reports customers.create", capabilitiesOf(adminLogin).includes(CAPABILITY));
    const byAdmin = await tryCreate(adminLogin, adminToken, 'ADM');
    check('server: an Admin may POST a customer', byAdmin.status === 201, String(byAdmin.status));
    check("server: the Admin's person is given a code the server drew", PERSON_CODE.test(byAdmin.code ?? ''), String(byAdmin.code));
    const manager = await apiLogin(MANAGER);
    check("server: a Manager's login reports customers.create", capabilitiesOf(manager).includes(CAPABILITY));
    const byManager = await tryCreate(manager, adminToken, 'MGR');
    check('server: a Manager may POST a customer', byManager.status === 201, String(byManager.status));
    check("server: the Manager's person is given a code the server drew", PERSON_CODE.test(byManager.code ?? ''), String(byManager.code));
    const cashier = await apiLogin(CASHIER);
    check("server: a Cashier's login does not report it", !capabilitiesOf(cashier).includes(CAPABILITY));
    check("server: nor is it among the Cashier's permissions", !(cashier.user.permissions ?? []).includes(CAPABILITY));
    const { status } = await tryCreate(cashier, adminToken, 'CSH');
    check("server: a Cashier's POST is refused", status === 403, String(status));
  }

  // ---------- Admin: rental contract form ----------
  {
    const { context, page } = await signIn(browser, ADMIN);
    await page.goto(BASE + '/#/backoffice/rentals/contracts/new');
    await page.waitForSelector('#rf-customer', { timeout: 20000 });
    const button = page.locator('.new-customer');
    check('rental form: an Admin online sees "+ New customer"', (await button.count()) === 1);

    await page.fill('#rf-notes', 'livraison lundi');
    await page.fill('#rf-signed', '2026-10-01');
    await page.locator('.contract-bar > .btn-ghost').first().click(); // Add line: a second line
    await mark(page, 'app-rental-contract-form');
    const typed = await rentalState(page);
    const before = await page.$eval('#rf-customer', (s) => s.value);

    await button.click();
    await page.waitForSelector('.bo-edit-modal', { timeout: 15000 });
    check('rental form: opening the editor keeps the form as typed', JSON.stringify(await rentalState(page)) === JSON.stringify(typed));
    await page.locator('.bo-edit-modal .bo-panel__foot .btn-ghost').click();
    await page.waitForSelector('.bo-edit-modal', { state: 'detached' });
    check('rental form: cancelling keeps every field as typed', JSON.stringify(await rentalState(page)) === JSON.stringify(typed), JSON.stringify(await rentalState(page)));
    check('rental form: cancelling keeps the customer as it was', (await page.$eval('#rf-customer', (s) => s.value)) === before);
    check('rental form: cancelling does not re-create the form', await stillSame(page, 'app-rental-contract-form'));

    await button.click();
    await page.waitForSelector('.bo-edit-modal', { timeout: 15000 });
    const name = await fillAndSave(page, 'RENT');
    await page.waitForTimeout(800);
    const chosen = await page.$eval('#rf-customer', (s) => s.options[s.selectedIndex]?.textContent?.trim());
    check('rental form: the new customer is selected', chosen === name, chosen);
    check('rental form: saving keeps every field as typed', JSON.stringify(await rentalState(page)) === JSON.stringify(typed), JSON.stringify(await rentalState(page)));
    check('rental form: saving does not re-create the form', await stillSame(page, 'app-rental-contract-form'));
    created.push(name);

    await context.setOffline(true);
    await page.evaluate(() => window.dispatchEvent(new Event('offline')));
    await page.waitForTimeout(300);
    check('rental form: offline, the button is gone', (await page.locator('.new-customer').count()) === 0);
    await context.close();
  }

  // ---------- Admin: the till ----------
  {
    const { context, page } = await signIn(browser, ADMIN);
    await page.goto(BASE + '/#/register');
    await page.waitForSelector('.tile .tile-add', { timeout: 20000 });
    const adds = page.locator('.tile:not(.out) .tile-add');
    await adds.nth(0).click();
    await adds.nth(0).click(); // quantity 2
    await adds.nth(1).click();
    await page.selectOption('#disc-type', 'percent');
    await page.fill('#disc-value', '5');
    await page.check('#taxmode');
    await page.waitForTimeout(300);
    await mark(page, 'app-register');
    const rung = await tillState(page);
    const lines = await page.locator('.cart .line').count();
    const button = page.locator('.cust-head button');
    check('till: an Admin online sees "+ New customer"', (await button.count()) === 1);

    await button.click();
    await page.waitForSelector('.bo-edit-modal', { timeout: 15000 });
    check('till: opening the editor keeps the sale as rung up', JSON.stringify(await tillState(page)) === JSON.stringify(rung));
    await page.locator('.bo-edit-modal .bo-panel__foot .btn-ghost').click();
    await page.waitForSelector('.bo-edit-modal', { state: 'detached' });
    check('till: cancelling keeps the cart, quantities, discount, tax choice and total', JSON.stringify(await tillState(page)) === JSON.stringify(rung), JSON.stringify(await tillState(page)));
    check('till: cancelling does not re-create the till', await stillSame(page, 'app-register'));
    check('till: cancelling keeps the walk-in customer', (await page.$eval('#cust', (s) => s.value)) === '');

    await button.click();
    await page.waitForSelector('.bo-edit-modal', { timeout: 15000 });
    const name = await fillAndSave(page, 'TILL');
    created.push(name);
    await page.waitForFunction(
      (n) => {
        const s = document.querySelector('#cust');
        return s && s.options[s.selectedIndex]?.textContent?.trim() === n;
      },
      name,
      { timeout: 20000 },
    ).then(() => check('till: the new customer is pulled and selected', true))
      .catch(() => check('till: the new customer is pulled and selected', false));
    check('till: saving keeps the cart, quantities, discount, tax choice and total', JSON.stringify(await tillState(page)) === JSON.stringify(rung), JSON.stringify(await tillState(page)));
    check('till: saving does not re-create the till', await stillSame(page, 'app-register'));
    check('till: the line count is unchanged', (await page.locator('.cart .line').count()) === lines);

    await context.setOffline(true);
    await page.evaluate(() => window.dispatchEvent(new Event('offline')));
    await page.waitForTimeout(300);
    check('till: offline, the button is gone', (await page.locator('.cust-head button').count()) === 0);
    await context.close();
  }

  // ---------- an old session: a stored login from before capabilities ----------
  {
    const { context, page } = await signIn(browser, ADMIN);
    await page.goto(BASE + '/#/register');
    await page.waitForSelector('#cust', { timeout: 20000 });
    check('old session: the current login offers the button', (await page.locator('.cust-head button').count()) === 1);
    // Take the field off the stored session and the stored user row, as an older build wrote them.
    await page.evaluate(async () => {
      const open = indexedDB.open('algeria-pos');
      const db = await new Promise((res, rej) => { open.onsuccess = () => res(open.result); open.onerror = rej; });
      const strip = (store, fix) => new Promise((res) => {
        const tx = db.transaction(store, 'readwrite');
        const os = tx.objectStore(store);
        const all = os.getAll();
        all.onsuccess = () => { for (const row of all.result) { fix(row); os.put(row); } };
        tx.oncomplete = res;
      });
      await strip('meta', (m) => { if (m.sessionBranch) delete m.sessionBranch.capabilities; });
      await strip('users', (u) => { delete u.capabilities; });
      db.close();
    });
    await page.reload({ waitUntil: 'networkidle' });
    await page.waitForSelector('#cust', { timeout: 20000 });
    check('old session: without capabilities the button is not offered', (await page.locator('.cust-head button').count()) === 0);
    check('old session: the till still works, on the walk-in customer', (await page.$eval('#cust', (s) => s.value)) === '');
    await context.close();
  }

  // ---------- a branch Manager ----------
  if (MANAGER) {
    const { context, page } = await signIn(browser, MANAGER);
    await page.goto(BASE + '/#/backoffice/rentals/contracts/new');
    await page.waitForSelector('#rf-customer', { timeout: 20000 });
    check('rental form: a Manager online sees "+ New customer"', (await page.locator('.new-customer').count()) === 1);
    await context.close();
  } else skip('Manager', 'set POS_MANAGER / POS_MANAGER_PASSWORD');

  // ---------- a Cashier ----------
  if (CASHIER) {
    const { context, page } = await signIn(browser, CASHIER);
    await page.goto(BASE + '/#/register');
    await page.waitForSelector('#cust', { timeout: 20000 });
    check('till: a Cashier does not see "+ New customer"', (await page.locator('.cust-head button').count()) === 0);
    await context.close();
  } else skip('Cashier', 'set POS_CASHIER / POS_CASHIER_PASSWORD');

  await browser.close();

  // ---------- clean up what was created ----------
  {
    const headers = { Authorization: `Bearer ${adminToken}` };
    const list = await (await fetch(BASE + '/api/catalog/customers', { headers })).json();
    // The customers made from the screens, found by the name typed: each has the code the server drew.
    const made = list.filter((x) => created.includes(x.fullName));
    check('server: each customer made from a screen has a code the server drew',
      made.length === created.length && made.every((x) => PERSON_CODE.test(x.code ?? '')),
      made.map((x) => x.code).join(' '));
    for (const c of list.filter((x) => created.includes(x.fullName) || x.fullName.startsWith('E2E FR-1 '))) {
      await fetch(`${BASE}/api/catalog/customers/${c.id}`, { method: 'DELETE', headers });
    }
    for (const id of tempUsers) await fetch(`${BASE}/api/users/${id}`, { method: 'DELETE', headers });
  }

  console.log(failures ? `\n${failures} CHECK(S) FAILED` : '\nALL CHECKS PASSED');
  process.exit(failures ? 1 : 0);
})();
