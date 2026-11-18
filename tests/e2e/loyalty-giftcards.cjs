// Real API test for gift cards and loyalty points. Run after: dotnet build ParadiERP.API
// Starts a single-shop API on a temporary SQLite database with the demo seed.
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { mkdtemp, rm } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const path = require('node:path');
const { randomUUID, createHash } = require('node:crypto');
const net = require('node:net');

(async () => {
  const testPassword = randomUUID();
  const temp = await mkdtemp(path.join(tmpdir(), 'algeria-pos-loyalty-'));
  const socket = net.createServer();
  await new Promise(resolve => socket.listen(0, '127.0.0.1', resolve));
  const port = socket.address().port;
  await new Promise(resolve => socket.close(resolve));
  const base = `http://127.0.0.1:${port}`;
  const configuration = process.env.BUILD_CONFIGURATION || 'Debug';
  const dll = path.resolve(__dirname, `../../ParadiERP.API/bin/${configuration}/net8.0/ParadiERP.API.dll`);
  const server = spawn('dotnet', [dll], {
    cwd: temp, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
    // RateLimits off: this script fires its checks back to back from one address (limits have their own tests).
    env: { ...process.env, RateLimits__Enabled: 'false', Bootstrap__AdminPassword: testPassword, ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: base,
      ConnectionStrings__Default: `Data Source=${path.join(temp, 'pos.db')}`, Logging__LogLevel__Default: 'Warning' },
  });
  let logs = '';
  server.stdout.on('data', d => { logs = (logs + d).slice(-8000); });
  server.stderr.on('data', d => { logs = (logs + d).slice(-8000); });
  let token = '';
  const call = async (route, method = 'GET', body) => {
    const res = await fetch(base + route, {
      method, headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await res.text();
    return { status: res.status, body: text ? (() => { try { return JSON.parse(text); } catch { return text; } })() : null };
  };
  try {
    let ready = false;
    // Up to 90 s, as saas-api.cjs: a fresh database is migrated through the whole history first.
    for (let i = 0; i < 450 && !ready; i++) {
      if (server.exitCode !== null) throw new Error(`API exited: ${logs}`);
      try { ready = (await fetch(base + '/api/auth/ping')).ok; } catch { await new Promise(r => setTimeout(r, 200)); }
    }
    assert.ok(ready, `API did not start: ${logs}`);
    const login = await call('/api/auth/login', 'POST', { username: 'admin', password: testPassword, deviceCode: 'C01' });
    assert.equal(login.status, 200);
    token = login.body.token;

    // ---- loyalty settings ----
    const store = (await call('/api/catalog/store')).body;
    assert.equal((await call('/api/catalog/store', 'PUT', { ...store, loyaltyEnabled: true, loyaltyPointValue: 0 })).status, 400, 'enabled programme needs a point value');
    const saved = await call('/api/catalog/store', 'PUT', { ...store, loyaltyEnabled: true, loyaltyPointsPerUnit: 0.1, loyaltyPointValue: 0.5, loyaltyMinRedeemPoints: 20 });
    assert.equal(saved.status, 200);
    assert.equal(saved.body.loyaltyPointValue, 0.5);
    const { loyaltyEnabled, loyaltyPointsPerUnit, loyaltyPointValue, loyaltyMinRedeemPoints, ...older } = store;
    const untouched = await call('/api/catalog/store', 'PUT', { ...older, receiptFooter: 'Merci' });
    assert.equal(untouched.body.loyaltyEnabled, true, 'older clients without loyalty fields keep the programme');

    // ---- gift cards ----
    const issued = await call('/api/catalog/gift-cards', 'POST', { code: 'gift-e2e', amount: 100, expiresAtUtc: null, customerId: null });
    assert.equal(issued.status, 201);
    assert.equal(issued.body.code, 'GIFT-E2E');
    assert.equal((await call('/api/catalog/gift-cards', 'POST', { code: 'GIFT-E2E', amount: 5 })).status, 409);
    assert.equal((await call('/api/catalog/gift-cards', 'POST', { amount: 0 })).status, 400);
    const generated = await call('/api/catalog/gift-cards', 'POST', { amount: 20 });
    assert.match(generated.body.code, /^[A-Z2-9]{12}$/);
    const card = issued.body;

    // ---- the customer the points go to ----
    // Made here rather than taken from the seed by a typed code: the server draws a customer's code
    // (owner, 2026-10-05) — Cust-, a location's three digits, YYMM, a sequence: Cust-3612610001 — and
    // the till finds it by the code it was given.
    const made = await call('/api/catalog/customers', 'POST', { fullName: 'Client Fidélité E2E', creditLimit: 0, kind: 'Person' });
    assert.equal(made.status, 201, JSON.stringify(made.body));
    assert.match(made.body.code, /^Cust-[1-9]\d{2}\d{4}\d{3,}$/, 'a person is given a code the server drew');

    const pull = (await call('/api/sync/pull?since=0&deviceCode=C01')).body;
    assert.equal(pull.giftCards.length, 2, 'gift cards replicate to tills');
    assert.equal(pull.store.loyaltyEnabled, true);
    const customer = pull.customers.find(c => c.code === made.body.code);
    assert.equal(customer?.id, made.body.id, 'the new customer replicates to tills under its drawn code');
    assert.equal(customer.loyaltyPoints, 0);
    const product = pull.products.find(p => p.price > 0);

    let seq = 0;
    const sale = (total, payments, extra = {}) => {
      seq++;
      return {
        id: randomUUID(), reference: `C01-${String(seq).padStart(6, '0')}`, localSeq: seq, soldAtUtc: new Date().toISOString(),
        customerId: customer.id, cashierUserId: login.body.user.id, cashierName: 'Admin',
        subTotalHt: total, taxTotal: 0, discountTotal: 0, grandTotalTtc: total, roundingAdjustment: 0,
        paidAmount: total, changeAmount: 0, paymentMethod: payments.length > 1 ? 'Mixed' : payments[0].method,
        status: 'Completed', refundOfSaleId: null, note: null, taxMode: 'Untaxed',
        // At the catalogue's price, the quantity making up the total: a price unlike the catalogue's is
        // said on the push (security review, 2026-10-10), and these sales are meant to come back clean.
        lines: [{ id: randomUUID(), productId: product.id, sku: product.sku, nameSnapshot: 'x', qty: total / product.price, unitPrice: product.price,
          taxRate: 0, lineDiscount: 0, lineTotalTtc: total, lineTaxAmount: 0 }],
        payments: payments.map(p => ({ id: randomUUID(), reference: null, ...p })),
        ...extra,
      };
    };
    const push = async (s) => {
      const res = await call('/api/sync/push', 'POST', { deviceCode: 'C01', sales: [s] });
      assert.equal(res.status, 200);
      assert.equal(res.body.results[0].status, 'accepted', JSON.stringify(res.body));
      return res.body.results[0].message;
    };
    const state = async () => {
      const cards = (await call('/api/catalog/gift-cards?q=gift-e2e')).body;
      const customers = (await call('/api/catalog/customers')).body;
      return { card: cards[0].balance, points: customers.find(c => c.id === customer.id).loyaltyPoints };
    };

    // 500 = 440 cash + 60 on the card (code typed in lower case): card 40, points floor(440 × 0.1) = 44.
    const first = sale(500, [{ method: 'Cash', amount: 440 }, { method: 'GiftCard', amount: 60, reference: 'gift-e2e' }]);
    assert.equal(await push(first), null);
    assert.deepEqual(await state(), { card: 40, points: 44 });

    // 20 = 10 in points (20 points) + 10 cash (1 point earned): 44 − 20 + 1 = 25.
    await push(sale(20, [{ method: 'Loyalty', amount: 10 }, { method: 'Cash', amount: 10 }]));
    assert.deepEqual(await state(), { card: 40, points: 25 });

    // Overspending the card and an unknown card are accepted with warnings, never rejected.
    const overspend = await push(sale(50, [{ method: 'GiftCard', amount: 50, reference: 'GIFT-E2E' }], { customerId: null }));
    assert.match(overspend, /GIFT-E2E: insufficient/);
    assert.match(await push(sale(30, [{ method: 'GiftCard', amount: 5, reference: 'NOPE' }, { method: 'Cash', amount: 25 }], { customerId: null })), /Unknown gift card 'NOPE'/);
    assert.deepEqual(await state(), { card: -10, points: 25 });

    // Points are never refused either.
    assert.match(await push(sale(100, [{ method: 'Loyalty', amount: 100 }])), /loyalty points went negative/);
    assert.deepEqual(await state(), { card: -10, points: -175 });

    // Refund 100 of the first sale onto the card: card +100, points −10 (refunds take back what they earned).
    // A refund stands on its sale and never gives back more of an article than was sold (security
    // review, 2026-10-10): 100 of 500 is a fifth of the line, 10 a fiftieth.
    const refundOf = (total) => [{ id: randomUUID(), productId: product.id, sku: product.sku, nameSnapshot: 'x',
      qty: total / product.price, unitPrice: product.price, taxRate: 0, lineDiscount: 0, lineTotalTtc: total, lineTaxAmount: 0 }];
    await push(sale(100, [{ method: 'GiftCard', amount: 100, reference: 'GIFT-E2E' }], { status: 'Refunded', refundOfSaleId: first.id, lines: refundOf(100) }));
    assert.deepEqual(await state(), { card: 90, points: -185 });
    // A refund paid back in points returns the points: 10 → +20.
    await push(sale(10, [{ method: 'Loyalty', amount: 10 }], { status: 'Refunded', refundOfSaleId: first.id, lines: refundOf(10) }));
    assert.deepEqual(await state(), { card: 90, points: -165 });

    // ---- back-office adjustments and history ----
    assert.equal((await call(`/api/catalog/gift-cards/${card.id}/adjust`, 'POST', { delta: -500, reason: 'x' })).status, 400, 'no negative balance by hand');
    assert.equal((await call(`/api/catalog/gift-cards/${card.id}/adjust`, 'POST', { delta: 10, reason: '' })).status, 400, 'reason required');
    assert.equal((await call(`/api/catalog/gift-cards/${card.id}/adjust`, 'POST', { delta: 10, reason: 'Goodwill' })).body.balance, 100);
    const moves = (await call(`/api/catalog/gift-cards/${card.id}/movements`)).body;
    assert.deepEqual(moves.map(m => m.reason).sort(), ['adjustment', 'issue', 'redeem', 'redeem', 'refund']);
    const loyaltyMoves = (await call(`/api/catalog/customers/${customer.id}/loyalty`)).body;
    assert.deepEqual(loyaltyMoves.map(m => m.reason).sort(), ['earn', 'earn', 'redeem', 'redeem', 'refund', 'refund-redeem']);
    const deactivated = await call(`/api/catalog/gift-cards/${card.id}`, 'PUT', { isActive: false, expiresAtUtc: null, customerId: null });
    assert.equal(deactivated.body.isActive, false);
    const delta = (await call(`/api/sync/pull?since=${pull.cursor}&deviceCode=C01`)).body;
    // A till holds the card by the hash of its code, never the code (security review, 2026-10-10).
    const cardHash = createHash('sha256').update('GIFT-E2E').digest('hex');
    const tillCard = delta.giftCards.find(g => g.codeHash === cardHash);
    assert.equal(tillCard.isActive, false, 'changes reach tills on the next pull');
    assert.ok(!delta.giftCards.some(g => g.code.includes('GIFT-E2E')), 'no card code reaches a till');
    assert.equal(delta.customers.find(c => c.id === customer.id).loyaltyPoints, -165);

    // ---- report ----
    const day = new Date().toISOString().slice(0, 10);
    const report = (await call(`/api/reports/summary?from=${day}T00:00:00Z&to=${day}T23:59:59Z`)).body;
    assert.equal(report.giftCardTotal, 60 + 50 + 5 - 100);
    assert.equal(report.loyaltyTotal, 10 + 100 - 10);
    console.log('PASS: loyalty settings, gift card issue/redeem/overspend/refund/adjust, points earn/redeem/refund, warnings, sync, report.');
  } finally {
    if (server.exitCode === null) {
      const stopped = new Promise(resolve => server.once('exit', resolve));
      server.kill();
      await stopped;
    }
    if (path.dirname(temp) === path.resolve(tmpdir()) && path.basename(temp).startsWith('algeria-pos-loyalty-'))
      await rm(temp, { recursive: true, force: true });
  }
})().catch(error => { console.error(error.stack || error.message); process.exitCode = 1; });
