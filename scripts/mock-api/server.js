const { requiredPassword } = require('../../tests/e2e/test-credentials.cjs');
const testPassword = requiredPassword();
// Mock of the ASP.NET Core contract, used to exercise the real Angular sync engine
// end-to-end (login -> pull -> offline sale -> reconnect -> push).
const http = require('http');
const fs = require('fs');
const path = require('path');
const bcrypt = require('../../AlgeriaPos.WebUI/node_modules/bcryptjs');

const DIST = require('path').resolve(__dirname, '../../AlgeriaPos.WebUI/dist/algeria-pos-web-ui/browser');

let counter = 0;
const next = () => ++counter;

const uid = (n) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;

const users = [
  // Only Cashier rows are replicated to terminals (admin/manager PIN hashes stay server-side).
  { id: uid(1), username: 'caisse1', fullName: 'Caissier 1', role: 'Cashier',
    pinHash: bcrypt.hashSync('1111', 10), isActive: true, version: next(), isDeleted: false },
];
const store = {
  id: uid(2), nameAr: 'متجر النموذج', nameFr: 'Magasin Démo', nameEn: 'Demo Store',
  address: '12 Rue Didouche Mourad, Alger', phone: '+213 21 00 00 00',
  fiscalIds: { nif: '000016001234567', nis: '000016009876543', rc: '16/00-1234567B21', ai: '16010112345' },
  receiptFooter: 'Merci', requireCustomerTaxId: false, version: next(), isDeleted: false,
};
const categories = [
  { id: uid(3), nameAr: 'مواد غذائية', nameFr: 'Alimentation', nameEn: 'Grocery', color: '#16a34a', sortOrder: 1, version: next(), isDeleted: false },
];
const products = [
  { id: uid(10), sku: 'P001', barcode: '6130000000017', nameAr: 'خبز', nameFr: 'Pain', nameEn: 'Bread',
    categoryId: uid(3), price: 15, taxRate: 9, unit: 'pcs', trackStock: true, stockQty: 200,
    reorderLevel: 10, isActive: true, version: next(), isDeleted: false },
  { id: uid(11), sku: 'P005', barcode: '6130000000055', nameAr: 'ماء', nameFr: 'Eau 1.5L', nameEn: 'Water',
    categoryId: uid(3), price: 35, taxRate: 19, unit: 'pcs', trackStock: true, stockQty: 300,
    reorderLevel: 10, isActive: true, version: next(), isDeleted: false },
];
const customers = [
  { id: uid(20), code: 'C000', fullName: 'Client de passage', phone: null, address: null, taxId: null,
    creditLimit: 0, balance: 0, version: next(), isDeleted: false },
];

// The five built-in tenders as the server seeds them. Serving these means the offline
// cycle drives the same code path a real till does, instead of the fallback.
const paymentMethods = [
  ['cash', 'Cash', 'Espèces', true],
  ['card', 'Card', 'Carte', false],
  ['credit', 'Credit', 'À crédit', false],
  ['giftcard', 'GiftCard', 'Carte cadeau', false],
  ['loyalty', 'Loyalty', 'Points de fidélité', false],
].map(([key, behavior, nameFr, drawer], i) => ({
  id: key, key, nameAr: '', nameFr, nameEn: behavior, behavior,
  requiresConfirmation: false, affectsCashDrawer: drawer,
  requiresReference: key === 'giftcard', requiresInstrumentDetails: false,
  isBuiltIn: true, isActive: true, sortOrder: i + 1, version: 1, isDeleted: false,
}));

const giftCards = [
  { id: uid(30), code: 'GIFT-0001', initialAmount: 100, balance: 100, isActive: true, expiresAtUtc: null,
    customerId: null, version: next(), isDeleted: false },
];

const salesById = new Map();

// The cashier's own till, created with them on the server (2026-10-05). Another computer is taken
// to have sold on it up to this number already, so a sign-in has to continue after it.
const CASHIER_TILL = 'C02';
const SEEN_ELSEWHERE = { [CASHIER_TILL]: 41 };

/** The highest receipt sequence the server holds for a till, as the API's LastSequenceAsync. */
function lastSeq(code) {
  const pushed = [...salesById.values()]
    .filter((s) => (s.reference || '').startsWith(`${code}-`))
    .map((s) => Number(s.localSeq) || 0);
  return Math.max(SEEN_ELSEWHERE[code] ?? 0, ...pushed, 0);
}

// Captured before any sale moves them; a reset puts the catalog back to exactly this.
const seedStockBySku = Object.fromEntries(products.map((p) => [p.sku, p.stockQty]));
const pushLog = [];

function json(res, code, body) {
  const payload = JSON.stringify(body);
  res.writeHead(code, { 'Content-Type': 'application/json', 'Access-Control-Allow-Origin': '*' });
  res.end(payload);
}

function readBody(req) {
  return new Promise((resolve) => {
    let data = '';
    req.on('data', (c) => (data += c));
    req.on('end', () => resolve(data ? JSON.parse(data) : {}));
  });
}

const MIME = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css',
  '.ico': 'image/x-icon', '.png': 'image/png', '.webmanifest': 'application/manifest+json',
  '.json': 'application/json', '.txt': 'text/plain' };

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');

  if (req.method === 'OPTIONS') {
    res.writeHead(204, {
      'Access-Control-Allow-Origin': '*',
      'Access-Control-Allow-Headers': '*',
      'Access-Control-Allow-Methods': '*',
    });
    return res.end();
  }

  if (url.pathname === '/api/auth/ping') return json(res, 200, { ok: true, serverTimeUtc: new Date().toISOString() });

  if (url.pathname === '/api/auth/login' && req.method === 'POST') {
    const body = await readBody(req);
    const typed = (body.deviceCode || '').trim().toUpperCase();
    const expiresAtUtc = new Date(Date.now() + 30 * 864e5).toISOString();
    if (body.username === 'admin' && body.password === testPassword) {
      return json(res, 200, {
        token: 'mock-token', expiresAtUtc,
        user: { id: uid(99), username: 'admin', fullName: 'Administrateur', role: 'Admin' },
        // As the API: no till code is an office sign-in (2026-10-05), not "C01".
        deviceCode: typed,
        lastLocalSeq: typed ? lastSeq(typed) : 0,
        integrations: INTEGRATIONS,
      });
    }
    if (body.username === 'caisse1' && body.password === 'Caisse@123') {
      // As the API: a cashier who types no code opens their own till (2026-10-05).
      const code = typed || CASHIER_TILL;
      return json(res, 200, {
        token: 'mock-token', expiresAtUtc,
        user: { id: uid(1), username: 'caisse1', fullName: 'Caissier 1', role: 'Cashier',
          ...(CASHIER_CAPABILITIES ? { capabilities: CASHIER_CAPABILITIES } : {}) },
        deviceCode: code, lastLocalSeq: lastSeq(code), integrations: INTEGRATIONS,
      });
    }
    return json(res, 401, {});
  }

  if (url.pathname === '/api/sync/pull') {
    const since = Number(url.searchParams.get('since') || 0);
    const cursor = counter;
    const after = (rows) => rows.filter((r) => r.version > since && r.version <= cursor);
    return json(res, 200, {
      cursor,
      serverTimeUtc: new Date().toISOString(),
      store: store.version > since ? store : null,
      categories: after(categories),
      products: after(products),
      customers: after(customers),
      users: after(users).filter((u) => u.role === 'Cashier'),
      giftCards: after(giftCards),
      paymentMethods: after(paymentMethods),
      hasMore: false,
    });
  }

  if (url.pathname === '/api/sync/push' && req.method === 'POST') {
    const body = await readBody(req);
    pushLog.push(body);
    const cursorBefore = counter;
    const results = [];
    let accepted = 0, duplicates = 0, rejected = 0;

    for (const sale of body.sales) {
      if (salesById.has(sale.id)) { duplicates++; results.push({ id: sale.id, status: 'duplicate', message: null }); continue; }

      const lineSum = round2(sale.lines.reduce((s, l) => s + l.lineTotalTtc, 0));
      const taxSum = round2(sale.lines.reduce((s, l) => s + l.lineTaxAmount, 0));
      if (Math.abs(lineSum - sale.grandTotalTtc) > 0.01) {
        rejected++; results.push({ id: sale.id, status: 'rejected', message: `Total mismatch ${lineSum} != ${sale.grandTotalTtc}` }); continue;
      }
      if (Math.abs(taxSum - sale.taxTotal) > 0.01) {
        rejected++; results.push({ id: sale.id, status: 'rejected', message: `TVA mismatch ${taxSum} != ${sale.taxTotal}` }); continue;
      }

      salesById.set(sale.id, sale);
      for (const line of sale.lines) {
        const p = products.find((x) => x.id === line.productId);
        if (p && p.trackStock) { p.stockQty = round2(p.stockQty - line.qty); p.version = next(); }
      }
      accepted++;
      results.push({ id: sale.id, status: 'accepted', message: null });
    }
    return json(res, 200, { cursor: cursorBefore, serverTimeUtc: new Date().toISOString(), accepted, duplicates, rejected, results });
  }

  // Lets a test start from a known position instead of whatever the last run left behind.
  // The scripts assert absolute figures — two sales, Pain at 198 — so a second run against a
  // still-warm server fails on accumulated rows and looks exactly like a regression. That
  // false alarm has now cost two people an investigation each.
  if (url.pathname === '/__reset' && req.method === 'POST') {
    salesById.clear();
    pushLog.length = 0;
    for (const p of products) {
      if (p.trackStock) p.stockQty = seedStockBySku[p.sku];
    }
    return json(res, 200, { reset: true });
  }

  if (url.pathname === '/__state') {
    return json(res, 200, {
      sales: [...salesById.values()].map((s) => ({
        id: s.id, reference: s.reference, total: s.grandTotalTtc, tax: s.taxTotal, lines: s.lines.length, rounding: s.roundingAdjustment,
        method: s.paymentMethod, payments: (s.payments || []).map((p) => `${p.method}:${p.amount}`),
        references: (s.payments || []).map((p) => p.reference ?? null), taxMode: s.taxMode ?? null,
      })),
      pushCalls: pushLog.length,
      stock: products.map((p) => ({ sku: p.sku, stockQty: p.stockQty })),
    });
  }

  // static files
  let file = path.join(DIST, url.pathname === '/' ? 'index.html' : url.pathname);
  if (!fs.existsSync(file) || fs.statSync(file).isDirectory()) file = path.join(DIST, 'index.html');
  const ext = path.extname(file);
  res.writeHead(200, { 'Content-Type': MIME[ext] || 'application/octet-stream' });
  fs.createReadStream(file).pipe(res);
});

function round2(v) { return Math.round((v + Number.EPSILON) * 100) / 100; }

// Operator-configured integrations as a till may see them. Null unless a test sets
// MOCK_INTEGRATIONS, so requiring a card reference never changes the other e2e flows.
const INTEGRATIONS = process.env.MOCK_INTEGRATIONS ? JSON.parse(process.env.MOCK_INTEGRATIONS) : null;

// What the cashier's sign-in reports they may do, as the API's `capabilities`. Absent unless a test sets
// MOCK_CASHIER_CAPABILITIES (a JSON array), so the other e2e flows keep a till that has never been told
// "no untaxed sales" (sales.untaxed, 2026-11-02).
const CASHIER_CAPABILITIES = process.env.MOCK_CASHIER_CAPABILITIES ? JSON.parse(process.env.MOCK_CASHIER_CAPABILITIES) : null;

const PORT = Number(process.env.MOCK_PORT || 5099);
server.listen(PORT, () => console.log(`mock api on http://localhost:${PORT}`));
