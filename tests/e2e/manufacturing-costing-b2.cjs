// Manufacturing costing B2, against a running API on a disposable database with Manufacturing__StockOperations=true
// and Manufacturing__Costing=true, after tests/e2e/manufacturing-phase2.cjs completed an order on it: the order's
// frozen-cost panel before it has a snapshot, then B1 freezes it, late costs are recorded (recognized, unclassified,
// a case-variant duplicate), an expense arrives after the freeze — and the B2 screens are captured in French and
// Arabic at 1440 px and Arabic at 390 px, read only. Screenshots go to artifacts/manufacturing/costing-b2.
//
//   ADMIN_PASSWORD=... BASE=http://127.0.0.1:5198 node tests/e2e/manufacturing-phase2.cjs
//   ADMIN_PASSWORD=... BASE=http://127.0.0.1:5198 node tests/e2e/manufacturing-costing-b2.cjs
const { requiredPassword } = require('./test-credentials.cjs');
const password = requiredPassword();
const { chromium } = require('playwright');
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict'), http = require('node:http');
const crypto = require('node:crypto');

const api = process.env.BASE || 'http://127.0.0.1:5198';
const root = path.resolve(__dirname, '../..');
const out = path.join(root, 'artifacts/manufacturing/costing-b2');
fs.mkdirSync(out, { recursive: true });
const WEB_PORT = 5193;
let checks = 0;
const ok = (value, message) => { assert.ok(value, message); checks++; };
const eq = (actual, expected, message) => { assert.deepEqual(actual, expected, message); checks++; };

async function call(token, p, body, method = body ? 'POST' : 'GET') {
  const r = await fetch(api + p, {
    method,
    headers: { 'content-type': 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await r.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  return { status: r.status, data };
}

async function prepare() {
  const token = (await call(null, '/api/auth/login', { username: 'admin', password, deviceCode: 'C01' })).data.token;
  const req = async (p, body, method) => {
    const r = await call(token, p, body, method);
    if (r.status >= 300) throw Error(`${method ?? (body ? 'POST' : 'GET')} ${p}: ${r.status} ${JSON.stringify(r.data)}`);
    return r.data;
  };
  eq((await req('/api/manufacturing/operations/status')).costingEnabled, true, 'costing is on for this disposable database');
  const completed = (await req('/api/manufacturing/orders?status=Completed')).find(Boolean);
  ok(completed, 'the Phase 2 drive left a completed order');
  const id = completed.id ?? completed.order?.id;
  const before = await req(`/api/manufacturing/orders/${id}/cost-reconciliation`);
  eq(before.state, 'not_frozen', 'a completed order with no snapshot is "not frozen", not a zero cost');
  eq(before.figures, null, 'and has no figures');
  return { token, req, id };
}

async function freeze({ req, id }) {
  await req(`/api/manufacturing/orders/${id}/cost-snapshot`, { requestId: `b2-drive-${crypto.randomUUID()}`, reconstruct: false });
  const late = (kind, sourceReference, amount, reason) => req(`/api/manufacturing/orders/${id}/cost-snapshot/late-costs`, { kind, sourceReference, amount, reason });
  await late('expense', 'PV-B2-900', 120, 'Freight invoice approved after the freeze');
  await late('labour', 'PAY-2026-10', -30, 'October payroll approved');
  await late('freight-fix', 'INV-77', 50, 'Carrier correction');
  await late('expenses', 'pv-b2-900 ', 120, 'Same freight invoice, entered again');
  // An expense still recorded on the completed order after its freeze: drift that no late cost explains yet.
  await req(`/api/manufacturing/orders/${id}/expenses`, { description: 'Pallets', amount: 200, paymentVoucherId: null, clientRequestId: crypto.randomUUID() });

  const r = await req(`/api/manufacturing/orders/${id}/cost-reconciliation?live=true`);
  eq(r.state, 'frozen', 'the order is frozen');
  eq(r.figures.financiallyFinalized, null, 'financially finalized is never a number');
  eq(r.figures.adjustedProvisional, Math.round((r.figures.original - 30) * 1000) / 1000, 'only the recognized labour credit adjusts the original');
  eq([r.figures.lateUnclassified, r.figures.lateUncertain], [50, 240], 'unclassified and uncertain amounts stay apart');
  // Live − adjusted: the expense after the freeze (+200) and the recognized labour credit that today's records do not
  // reflect (+30) are both unexplained until a late cost or a correction explains them.
  const drift = Object.fromEntries(r.live.elements.map((e) => [e.element, e.unexplained]));
  eq([drift.expenses, drift.labour, r.live.unexplained], [200, 30, 230], 'the unexplained difference, element by element');
  for (const check of ['source_complete', 'source_hash', 'source_replay', 'element_identity', 'release_sum', 'unit_cost'])
    eq(r.checks.find((c) => c.code === check).result, 'pass', `${check} passes`);
  eq(r.checks.find((c) => c.code === 'late_sources').result, 'fail', 'the duplicated source fails its check, and nothing is repaired');
  const history = await req('/api/manufacturing/reports/cost-history');
  ok(history.rows.some((x) => x.orderId === id && x.state === 'frozen'), 'the history lists the frozen sheet');
}

function serve() {
  const web = path.join(root, 'AlgeriaPos.WebUI/dist/algeria-pos-web-ui/browser');
  const server = http.createServer(async (r, s) => {
    if (r.url.startsWith('/api/') || r.url === '/health') {
      const chunks = [];
      for await (const chunk of r) chunks.push(chunk);
      const headers = { ...r.headers };
      delete headers.host; delete headers.connection; delete headers['content-length'];
      const back = await fetch(api + r.url, { method: r.method, headers, body: ['GET', 'HEAD'].includes(r.method) ? undefined : Buffer.concat(chunks) });
      s.writeHead(back.status, { 'content-type': back.headers.get('content-type') || 'application/json' });
      s.end(Buffer.from(await back.arrayBuffer()));
      return;
    }
    const file = path.resolve(web, '.' + (r.url === '/' ? '/index.html' : decodeURIComponent(r.url.split('?')[0])));
    if (!file.startsWith(web + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) { s.writeHead(404); s.end(); return; }
    s.setHeader('content-type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.woff2': 'font/woff2', '.svg': 'image/svg+xml' })[path.extname(file)] || 'application/octet-stream');
    fs.createReadStream(file).pipe(s);
  });
  return new Promise((resolve) => server.listen(WEB_PORT, '127.0.0.1', () => resolve(server)));
}

async function open(browser, lang, width) {
  const context = await browser.newContext({ viewport: { width, height: 1000 }, serviceWorkers: 'block' });
  await context.addInitScript((l) => localStorage.setItem('pos.lang', l), lang);
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  const base = `http://127.0.0.1:${WEB_PORT}`;
  await page.goto(`${base}/#/login`);
  await page.fill('#server', base);
  await page.fill('#username', 'admin');
  await page.fill('#password', password);
  await page.click('button[type=submit]');
  await page.waitForURL(/office|register/);
  return { context, page, errors, base };
}

async function capture(page, lang, width, name) {
  await page.waitForTimeout(500);
  eq(await page.evaluate(() => document.documentElement.dir), lang === 'ar' ? 'rtl' : 'ltr', `${lang} ${name}: reading direction`);
  ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `${lang} ${width} ${name}: nothing scrolls sideways`);
  eq(await page.evaluate(() => (document.body.innerText.match(/\b(mfg|perm)\.[a-zA-Z_.]+/g) || []).slice(0, 3)), [], `${lang} ${name}: no dictionary key shows untranslated`);
  await page.screenshot({ path: path.join(out, `${lang}-${width}-${name}.png`), fullPage: true });
}

async function shootBefore(browser, id) {
  const { context, page, errors, base } = await open(browser, 'ar', 1440);
  await page.goto(`${base}/#/backoffice/manufacturing/orders/${id}`);
  await page.waitForSelector('app-mfg-order-frozen-cost section', { timeout: 20000 });
  await capture(page, 'ar', 1440, 'order-no-snapshot');
  eq(errors, [], 'ar 1440 before the freeze: no page error');
  await context.close();
}

async function shoot(browser, lang, width, id) {
  const { context, page, errors, base } = await open(browser, lang, width);
  const tabs = [
    ['overview', 'overview', 'app-mfg-cost-reconciliation .ui-metric--lead'],
    ['history', 'history', 'app-mfg-cost-reconciliation tbody tr'],
    ['late', 'late', 'app-mfg-cost-reconciliation tbody tr'],
    ['reconciliation', 'reconciliation', 'app-mfg-cost-reconciliation tbody tr'],
    ['variances', 'reconciliation&view=variances', 'app-mfg-cost-reconciliation tbody tr'],
    ['scrap', 'reconciliation&view=scrap', 'app-mfg-cost-reconciliation tbody tr'],
    ['releases', 'releases', 'app-mfg-cost-reconciliation tbody tr'],
    ['integrity', 'integrity', 'app-mfg-cost-reconciliation tbody tr'],
  ];
  const narrow = ['overview', 'history', 'late', 'integrity'];
  for (const [name, tab, ready] of width < 600 ? tabs.filter(([n]) => narrow.includes(n)) : tabs) {
    await page.goto(`${base}/#/backoffice/manufacturing/costing?tab=${tab}`);
    await page.waitForSelector(ready, { timeout: 20000 });
    await capture(page, lang, width, `costing-${name}`);
    if (name === 'overview') ok((await page.innerText('app-mfg-cost-reconciliation')).length > 0, `${lang} overview renders`);
  }
  await page.goto(`${base}/#/backoffice/manufacturing/orders/${id}`);
  await page.waitForSelector('app-mfg-order-frozen-cost .ui-metric--lead', { timeout: 20000 });
  await page.locator('app-mfg-order-frozen-cost input[type=checkbox]').check();
  await page.waitForSelector('app-mfg-order-frozen-cost .frozen-checks', { timeout: 20000 });
  await page.waitForTimeout(800);
  await capture(page, lang, width, 'order-frozen-cost');
  eq(errors, [], `${lang} ${width}: no page error`);
  await context.close();
}

async function main() {
  const world = await prepare();
  const server = await serve();
  const browser = await chromium.launch({ headless: true, channel: 'chrome' });
  try {
    await shootBefore(browser, world.id);
    await freeze(world);
    console.log(`PASS API: ${checks} assertions`);
    await shoot(browser, 'fr', 1440, world.id);
    await shoot(browser, 'ar', 1440, world.id);
    await shoot(browser, 'ar', 390, world.id);
    console.log(`PASS ${checks} B2 assertions; screenshots in ${path.relative(root, out)}`);
  } finally {
    await browser.close();
    await new Promise((resolve) => server.close(resolve));
  }
}

main().catch((e) => { console.error(e); process.exitCode = 1; });
