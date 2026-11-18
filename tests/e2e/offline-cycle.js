const { requiredPassword } = require('./test-credentials.cjs');
const testPassword = requiredPassword();
const { chromium } = require('playwright');

const BASE = 'http://localhost:5099';

(async () => {
  const browser = await chromium.launch({
    args: ['--no-sandbox', '--disable-dev-shm-usage'],
  });
  const context = await browser.newContext();
  const page = await context.newPage();
  page.on('pageerror', (e) => console.log('   PAGE ERROR:', e.message));

  let failures = 0;
  const check = (name, ok, extra = '') => {
    console.log(`${ok ? '✅' : '❌'} ${name}${extra ? ' — ' + extra : ''}`);
    if (!ok) failures++;
  };

  // Sign out lives in the till header's user menu since the 2026-10 redesign, and asks first
  // since 2026-10-05 (the owner): answer the question, then the login screen comes up.
  // A shared counter till's code sits behind a toggle on the online form (2026-10-05).
  const openSharedTill = async (code) => {
    if (await page.locator('#device').count() === 0) await page.click('.shared-toggle');
    await page.fill('#device', code);
  };

  const signOut = async () => {
    await page.click('.tools .user-trigger');
    await page.click('.tools .sign-out');
    await page.click('app-confirm-dialog .btn-primary');
    await page.waitForSelector('.login-wrap');
  };

  // Start from a known server state. Every assertion below is an absolute figure — two
  // sales, Pain at 198 — so without this a second run against a still-warm mock fails on
  // rows the previous run left, which reads as a regression and is not one.
  await fetch(`${BASE}/__reset`, { method: 'POST' });

  // ---- 1. boot -------------------------------------------------------------
  await page.goto(BASE, { waitUntil: 'networkidle' });
  await page.waitForSelector('.login-wrap', { timeout: 15000 });
  check('app boots to the login screen', true);

  const frTitle = await page.textContent('.head h1');
  check('default language is French', frTitle.trim() === 'Connexion', frTitle);

  // ---- 2. RTL --------------------------------------------------------------
  // A language change asks first, then switches after a five-second countdown (owner, 2026-10-12), and the
  // switch is cross-faded (a view transition), so it lands a frame or more after that.
  const switchLanguage = async (code, dir) => {
    await page.selectOption('select.lang', code);
    await page.click('app-confirm-dialog .btn-primary');
    await page.waitForFunction((d) => document.documentElement.dir === d, dir, { timeout: 12000 });
  };
  await switchLanguage('ar', 'rtl').catch(() => {});
  const dir = await page.getAttribute('html', 'dir');
  const arTitle = await page.textContent('.head h1');
  check('Arabic switches the document to RTL', dir === 'rtl', `dir=${dir}`);
  check('Arabic strings render', arTitle.includes('تسجيل'), arTitle);
  await switchLanguage('fr', 'ltr');

  // ---- 3. online login + first catalog pull ---------------------------------
  await page.fill('#server', BASE);
  await page.fill('#username', 'admin');
  await page.fill('#password', testPassword);
  // The admin opens the shared counter till by its code — behind the "shared till?" toggle since
  // 2026-10-05: with no code an admin gets the back office, and a cashier their own till.
  check('online sign-in asks for no till code', (await page.locator('#device').count()) === 0);
  await openSharedTill('C01');
  await page.click('button[type=submit]');
  await page.waitForSelector('.register', { timeout: 20000 });
  const tiles = await page.locator('.tile').count();
  check('login pulls the catalog into IndexedDB', tiles === 2, `${tiles} product tiles`);

  const persisted = await page.evaluate(async () => {
    const req = indexedDB.open('algeria-pos');
    const db = await new Promise((res) => (req.onsuccess = () => res(req.result)));
    const tx = db.transaction('products', 'readonly');
    const all = await new Promise((res) => {
      const r = tx.objectStore('products').getAll();
      r.onsuccess = () => res(r.result);
    });
    return all.length;
  });
  check('products are written to IndexedDB', persisted === 2, `${persisted} rows`);

  // ---- 4. go offline and sell ----------------------------------------------
  await context.setOffline(true);
  await page.evaluate(() => window.dispatchEvent(new Event('offline')));
  await page.waitForTimeout(600);
  const offlineBadge = await page.textContent('.conn');
  check('UI reports offline', offlineBadge.includes('Hors ligne'), offlineBadge.trim());

  // 2 x Pain (15 DA @ 9%) + 1 x Eau (35 DA @ 19%). The + is the only thing on a tile that adds
  // (register.component.html, since 2026-09-23); a click on the card itself adds nothing.
  await page.locator('.tile', { hasText: 'Pain' }).locator('.tile-add').click();
  await page.locator('.tile', { hasText: 'Pain' }).locator('.tile-add').click();
  await page.locator('.tile', { hasText: 'Eau' }).locator('.tile-add').click();

  await page.waitForFunction(() => document.querySelectorAll('.cart .line').length === 2, null, { timeout: 5000 });
  const total = await page.textContent('.grand dd');
  check('cart totals while offline', total.replace(/\s| | /g, '').startsWith('65,00'), total.trim());
  // A sale is untaxed until the cashier ticks it (the owner, 2026-09-25). Ticked, the stored
  // net prices get each product's own rate added on top: 2 x 15 @ 9% + 35 @ 19% = 74,35.
  const figure = (s) => s.replace(/[\s\u00a0\u202f]/g, '');
  const totalBecomes = (text) => page.waitForFunction(
    (t) => (document.querySelector('.grand dd')?.textContent ?? '').replace(/[\s  ]/g, '').startsWith(t),
    text, { timeout: 5000 }).catch(() => {});
  await page.check('#taxmode');
  await totalBecomes('74,35');
  const taxedTotal = await page.textContent('.grand dd');
  const taxLines = await page.locator('.totals dt').allTextContents();
  check('taxed: both TVA rates are shown in the cart',
    taxLines.some((t) => / 9%/.test(t)) && taxLines.some((t) => t.includes('19%')), taxLines.join(' | '));
  check('taxed: the rates are added on top of the net prices', figure(taxedTotal).startsWith('74,35'), taxedTotal.trim());
  await page.uncheck('#taxmode');
  await totalBecomes('65,00');
  const untaxedAgain = await page.textContent('.grand dd');
  check('untaxed again: back to the net total', figure(untaxedAgain).startsWith('65,00'), untaxedAgain.trim());

  await page.click('button.pay');
  await page.waitForSelector('.modal');
  await page.fill('#received', '100');
  const change = await page.textContent('.change strong');
  check('change is computed', change.replace(/\s| | /g, '').startsWith('35,00'), change.trim());

  await page.click('.actions .btn-primary');
  await page.waitForSelector('.done', { timeout: 10000 });
  const savedOffline = await page.locator('.done .badge').textContent();
  check('sale completes with no network', savedOffline.includes('localement'), savedOffline.trim());

  const receiptRef = await page.textContent('#receipt-print .meta b');
  check('receipt number is device-scoped', /^C01-\d{6}$/.test(receiptRef.trim()), receiptRef.trim());

  const receiptText = await page.textContent('#receipt-print');
  check('untaxed receipt carries no TVA rate', !receiptText.includes('19%') && !/ 9%/.test(receiptText));

  await page.click('.actions .btn-primary'); // new sale

  // a second offline sale, to prove the outbox queues more than one
  await page.locator('.tile', { hasText: 'Eau' }).locator('.tile-add').click();
  await page.click('button.pay');
  await page.waitForSelector('.modal');
  await page.click('.actions .btn-primary');
  await page.waitForSelector('.done');
  await page.click('.actions .btn-primary');

  const pendingBadge = await page.textContent('.nav .pill');
  check('outbox shows 2 pending sales', pendingBadge.trim() === '2', pendingBadge.trim());

  // survives a reload while still offline
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForSelector('.register', { timeout: 20000 });
  const pendingAfterReload = await page.textContent('.nav .pill');
  check('outbox survives a page reload', pendingAfterReload.trim() === '2', pendingAfterReload.trim());

  // ---- 5. reconnect and drain ----------------------------------------------
  await context.setOffline(false);
  await page.evaluate(() => window.dispatchEvent(new Event('online')));

  await page.waitForFunction(() => !document.querySelector('.nav .pill'), null, { timeout: 30000 })
    .then(() => check('outbox drains automatically on reconnect', true))
    .catch(() => check('outbox drains automatically on reconnect', false, 'still pending after 30s'));

  const state = await (await fetch(`${BASE}/__state`)).json();
  check('server received both sales', state.sales.length === 2, JSON.stringify(state.sales));
  check('cash rounding is uploaded with each sale', state.sales.every((s) => s.rounding === 0));
  const totals = state.sales.map((s) => s.total).sort((a, b) => a - b);
  check('server totals match the till', JSON.stringify(totals) === JSON.stringify([35, 65]), JSON.stringify(totals));

  const bread = state.stock.find((s) => s.sku === 'P001');
  const water = state.stock.find((s) => s.sku === 'P005');
  check('server stock decremented', bread.stockQty === 198 && water.stockQty === 298,
    `Pain=${bread.stockQty} Eau=${water.stockQty}`);

  // ---- 6. idempotency: replay the same batch --------------------------------
  const replay = state.sales[0];
  const first = await (await fetch(`${BASE}/api/sync/push`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ deviceCode: 'C01', sales: [{ ...replayPayload(replay) }] }),
  })).json();
  check('re-pushing a synced sale is a no-op', first.duplicates === 1 && first.accepted === 0,
    JSON.stringify({ accepted: first.accepted, duplicates: first.duplicates }));

  const after = await (await fetch(`${BASE}/__state`)).json();
  check('no double-recording after replay', after.sales.length === 2, `${after.sales.length} sales`);

  // ---- 7. offline PIN unlock ----------------------------------------------
  await signOut();
  await context.setOffline(true);
  await page.evaluate(() => window.dispatchEvent(new Event('offline')));
  await page.waitForTimeout(400);

  const hasOfflineTab = await page.locator('.tabs button', { hasText: 'PIN' }).count();
  check('offline PIN tab appears once a cashier has synced', hasOfflineTab === 1);
  if (hasOfflineTab) {
    await page.locator('.tabs button', { hasText: 'PIN' }).click();
    const tillNote = (await page.textContent('.till-note').catch(() => '')) ?? '';
    check('offline, the form names the till this computer last opened', tillNote.includes('C01'), tillNote.trim());
    check('offline sign-in asks for no till code', (await page.locator('#device').count()) === 0);
    await page.fill('#pin', '9999');
    await page.click('button[type=submit]');
    await page.waitForTimeout(800);
    const stillOnLogin = await page.locator('.login-wrap').count();
    check('wrong PIN is refused offline', stillOnLogin === 1);

    await page.fill('#pin', '1111');
    await page.click('button[type=submit]');
    await page.waitForSelector('.register', { timeout: 15000 })
      .then(() => check('correct PIN opens the register with no network', true))
      .catch(() => check('correct PIN opens the register with no network', false));
  }
  await context.setOffline(false);
  await page.evaluate(() => window.dispatchEvent(new Event('online')));
  await page.waitForTimeout(500);

  // ---- 8. sync page --------------------------------------------------------
  await page.click('a[href*="sync"]');
  await page.waitForSelector('.tiles');
  const syncedTile = await page.locator('.tile strong').nth(2).textContent();
  check('sync page reports 2 synced sales', syncedTile.trim() === '2', syncedTile.trim());

  // ---- 9. split payment ----------------------------------------------------
  await page.click('a[href*="register"]');
  await page.waitForSelector('.register');
  await page.locator('.tile', { hasText: 'Eau' }).locator('.tile-add').click();
  await page.click('button.pay');
  await page.locator('.split-toggle').click();
  // Tenders are configurable now, so the dropdown carries the definition key rather than
  // the built-in name — an operator-defined tender has no built-in name to carry.
  await page.locator('.part select').nth(0).selectOption('card');
  await page.locator('.part input').nth(0).fill('20');
  await page.locator('.part select').nth(1).selectOption('cash');
  await page.locator('.part input').nth(1).fill('50');
  const splitChange = await page.locator('.change strong').nth(1).textContent();
  check('split payment computes change from cash', splitChange.replace(/\s| | /g, '').startsWith('35,00'), splitChange.trim());
  await page.click('.actions .btn-primary');
  await page.waitForSelector('.done', { timeout: 10000 });
  await page.click('.actions .btn-primary');

  // The cashier was unlocked by PIN after the Admin signed out, and an Admin's sign-out
  // removes the till's server token (no administrative access left on a shared till). So the
  // sale is kept and waits in the outbox — the network being back is not someone signing in.
  await page.waitForTimeout(1500);
  const waiting = await page.textContent('.nav .pill').catch(() => '');
  const beforeSignIn = await (await fetch(`${BASE}/__state`)).json();
  check('a sale made in a PIN session with no server token waits in the outbox',
    (waiting ?? '').trim() === '1' && !beforeSignIn.sales.some((s) => s.method === 'Mixed'), `pending ${waiting}`);

  // An online sign-in brings a token back, and the waiting sale goes up with it.
  await signOut();
  const passwordTab = page.locator('.tabs button').first();
  if (await page.locator('#password').count() === 0 && await passwordTab.count()) await passwordTab.click();
  if (await page.locator('#server').count()) await page.fill('#server', BASE);
  await page.fill('#username', 'admin');
  await page.fill('#password', testPassword);
  await openSharedTill('C01');
  await page.click('button[type=submit]');
  await page.waitForSelector('.register', { timeout: 20000 });
  await page.waitForFunction(() => !document.querySelector('.nav .pill'), null, { timeout: 30000 }).catch(() => {});
  const splitState = await (await fetch(`${BASE}/__state`)).json();
  const mixed = splitState.sales.find((s) => s.method === 'Mixed');
  // Cash reaches the server as tendered — 50, not 50 less the 35 handed back. The server is
  // what takes change off the drawer; the till used to take it off as well, so every branch's
  // recorded cash ran short by exactly the change it had given out.
  check('split sale reaches the server as tendered', !!mixed && mixed.payments.join(' ') === 'Card:20 Cash:50', JSON.stringify(mixed));

  // ---- 10. a cashier signs in with no code, on their own till ---------------
  // The till comes with the cashier (2026-10-05). The mock, as the API, puts caisse1 on C02 and
  // says another computer already sold on it up to 41, so the next receipt has to be C02-000042.
  await signOut();
  if (await page.locator('#password').count() === 0 && await passwordTab.count()) await passwordTab.click();
  await page.fill('#username', 'caisse1');
  await page.fill('#password', 'Caisse@123');
  await page.click('button[type=submit]');
  await page.waitForSelector('.register', { timeout: 20000 })
    .then(() => check('a cashier with no till code lands on the till', true))
    .catch(() => check('a cashier with no till code lands on the till', false));
  await page.locator('.tile', { hasText: 'Pain' }).locator('.tile-add').click();
  await page.click('button.pay');
  await page.waitForSelector('.modal');
  await page.click('.actions .btn-primary');
  await page.waitForSelector('.done', { timeout: 10000 });
  const ownRef = ((await page.textContent('#receipt-print .meta b')) ?? '').trim();
  check('the cashier sells on their own till, after the number another computer reached', ownRef === 'C02-000042', ownRef);
  await page.click('.actions .btn-primary');

  await browser.close();
  console.log(failures === 0 ? '\nALL CHECKS PASSED' : `\n${failures} CHECK(S) FAILED`);
  process.exit(failures === 0 ? 0 : 1);

  function replayPayload(s) {
    return { id: s.id, reference: s.reference, localSeq: 1, soldAtUtc: new Date().toISOString(),
      customerId: null, cashierUserId: '00000000-0000-4000-8000-000000000001', cashierName: 'x',
      subTotalHt: 0, taxTotal: 0, discountTotal: 0, grandTotalTtc: 0, paidAmount: 0, changeAmount: 0,
      paymentMethod: 'Cash', status: 'Completed', refundOfSaleId: null, note: null, lines: [], payments: [] };
  }
})();
