// Real API smoke test. Run after: dotnet build ParadisERP.API
// Uses isolated temporary databases; no existing company configuration is modified.
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { mkdtemp, rm, writeFile } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const path = require('node:path');
const { randomBytes } = require('node:crypto');
const net = require('node:net');
const http = require('node:http');

(async () => {
  const temp = await mkdtemp(path.join(tmpdir(), 'algeria-pos-saas-'));
  const socket = net.createServer();
  await new Promise(resolve => socket.listen(0, '127.0.0.1', resolve));
  const port = socket.address().port;
  await new Promise(resolve => socket.close(resolve));
  const password = randomBytes(24).toString('hex');
  // Loaded from the API's content root: an uploaded-style language and a full profile for
  // company 1 (Tunisia, cash rounding to 0.1, French + Tamazight).
  const text = (fr) => ({ fr });
  await writeFile(path.join(temp, 'appsettings.Local.json'), JSON.stringify({
    LanguagePacks: [{ code: 'tzm', label: 'Tamaziɣt', fallbackLanguage: 'fr', strings: { 'login.title': 'Tuqqna' } }],
    Tenancy: { Companies: [{}, { Regional: {
      countryCode: 'TN', currencyCode: 'TND', currencyDecimals: 3, currencySymbols: text('DT'), timeZone: 'Africa/Tunis',
      languages: ['fr', 'tzm'], defaultLanguage: 'fr', taxNames: text('TVA'), taxRates: [19, 7, 0], defaultTaxRate: 19,
      taxRateLabels: [{ rate: 7, labels: text('Taux réduit') }], fiscalIds: [], cashDenominations: [5, 10],
      cashRoundingIncrement: 0.1, cashRoundingMode: 'Nearest', paymentMethods: ['Cash'],
      receiptSections: ['header', 'legal', 'lines', 'totals'], receiptLegalText: text('Mention légale'),
    } }] },
  }));
  // RateLimits off: this script fires its checks back to back from one address (limits have their own tests).
  const env = { ...process.env, RateLimits__Enabled: 'false', ASPNETCORE_ENVIRONMENT: 'Production', ASPNETCORE_URLS: `http://127.0.0.1:${port}`,
    Jwt__Key: randomBytes(32).toString('hex'), Logging__LogLevel__Default: 'Warning' };
  for (let i = 0; i < 2; i++) {
    const prefix = `Tenancy__Companies__${i}__`;
    Object.assign(env, Object.fromEntries(Object.entries({
      Id: `company-${i}`, Hosts__0: `company-${i}.test`, DatabaseProvider: 'Sqlite',
      ConnectionString: `Data Source=${path.join(temp, `company-${i}.db`)}`,
      BootstrapAdminPassword: password, Subscription__Plan: 'Test',
      Subscription__Status: i ? 'Suspended' : 'Active', Subscription__ExpiresAtUtc: '2099-01-01T00:00:00Z',
      Subscription__MaxUsers: '2', Subscription__MaxBranches: '1', Subscription__MaxDevices: '1',
      ...(i ? { Country: 'TN' } : {}),
    }).map(([key, value]) => [prefix + key, value])));
  }
  const configuration = process.env.BUILD_CONFIGURATION || 'Debug';
  const dll = path.resolve(__dirname, `../../ParadisERP.API/bin/${configuration}/net8.0/ParadisERP.API.dll`);
  const server = spawn('dotnet', [dll], { cwd: temp, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  let logs = '';
  server.stdout.on('data', data => { logs = (logs + data).slice(-12000); });
  server.stderr.on('data', data => { logs = (logs + data).slice(-12000); });
  let launchError;
  server.on('error', error => { launchError = error; });
  async function request(company, route, method = 'GET', body, token) {
    return new Promise((resolve, reject) => {
      const req = http.request({ hostname: '127.0.0.1', port, path: route, method,
        headers: { Host: `company-${company}.test`, 'Content-Type': 'application/json',
          ...(token ? { Authorization: `Bearer ${token}` } : {}) } }, res => {
        let data = '';
        res.on('data', chunk => { data += chunk; });
        res.on('end', () => resolve({ status: res.statusCode, ok: res.statusCode >= 200 && res.statusCode < 300,
          json: async () => JSON.parse(data) }));
      });
      req.on('error', reject);
      req.setTimeout(10000, () => req.destroy(new Error('HTTP timeout')));
      req.end(body === undefined ? undefined : JSON.stringify(body));
    });
  }
  try {
    let ready = false;
    // Up to 90 s: the API migrates every company's fresh database through the whole history
    // before it answers, which took 24 s on the deploy machine on 2026-10-10 — past the 20 s
    // this waited before. It stops at the first answer, so a quick start is not slowed down.
    for (let i = 0; i < 450; i++) {
      if (launchError) throw launchError;
      if (server.exitCode !== null) throw new Error(`API exited: ${logs}`);
      try { if ((await request(0, '/api/auth/ping')).ok) { ready = true; break; } } catch {}
      await new Promise(resolve => setTimeout(resolve, 200));
    }
    assert.ok(ready, `API did not start: ${logs}`);
    const login = async company => {
      const response = await request(company, '/api/auth/login', 'POST', { username: 'admin', password, deviceCode: 'C01' });
      assert.equal(response.status, 200);
      return response.json();
    };
    const a = await login(0), b = await login(1);
    assert.notEqual(a.user.id, b.user.id);
    assert.equal((await request(1, '/api/catalog/store', 'GET', undefined, a.token)).status, 401, 'cross-company token');
    assert.equal((await request(9, '/api/auth/ping')).status, 404, 'unknown hostname');
    assert.equal((await request(1, '/api/catalog/store', 'GET', undefined, b.token)).status, 200, 'expired read access');
    const category = { nameAr: '', nameFr: 'Test', nameEn: 'Test', color: '#000000', sortOrder: 0 };
    assert.equal((await request(1, '/api/catalog/categories', 'POST', category, b.token)).status, 403, 'inactive write');
    assert.equal((await request(0, '/api/catalog/categories', 'POST', category, a.token)).status, 201);
    const bCategories = await (await request(1, '/api/catalog/categories', 'GET', undefined, b.token)).json();
    assert.equal(bCategories.length, 0, 'database isolation');
    assert.equal((await request(1, '/api/sync/push', 'POST', { deviceCode: 'C01', sales: [] }, b.token)).status, 200, 'inactive sync');
    const pull = await (await request(1, '/api/sync/pull?since=0&deviceCode=C01', 'GET', undefined, b.token)).json();
    assert.equal(pull.subscription.canOperate, false);
    assert.equal(a.regional.currencyCode, 'DZD', 'default country profile');
    assert.equal(b.regional.currencyCode, 'TND', 'configured country preset');
    assert.equal(pull.regional.currencyDecimals, 3, 'profile delivered on pull');
    assert.equal(pull.regional.cashRoundingIncrement, 0.1);
    assert.deepEqual(pull.regional.receiptSections, ['header', 'legal', 'lines', 'totals']);
    assert.equal(pull.regional.taxRateLabels[0].labels.fr, 'Taux réduit');
    assert.equal(typeof b.languagePacks.tzm, 'number', 'enabled pack revision on login');
    assert.deepEqual(a.languagePacks, {}, 'no packs for a company without extra languages');
    const pack = await request(1, '/api/i18n/tzm');
    assert.equal(pack.status, 200);
    assert.equal((await pack.json()).strings['login.title'], 'Tuqqna');
    assert.equal((await request(0, '/api/i18n/tzm')).status, 404, 'pack only served to companies that enable it');
    const soldAtUtc = new Date().toISOString();
    const roundedSale = {
      id: '00000000-0000-4000-8000-00000000c0de', reference: 'C01-000001', localSeq: 1, soldAtUtc, customerId: null,
      cashierUserId: b.user.id, cashierName: 'Admin', subTotalHt: 1.037, taxTotal: 0.197, discountTotal: 0,
      grandTotalTtc: 1.234, roundingAdjustment: -0.034, paidAmount: 2, changeAmount: 0.8,
      paymentMethod: 'Cash', status: 'Completed', refundOfSaleId: null, note: null,
      lines: [{ id: '00000000-0000-4000-8000-00000000c0df', productId: '00000000-0000-4000-8000-00000000c0e0', sku: 'X',
        nameSnapshot: 'X', qty: 1, unitPrice: 1.234, taxRate: 19, lineDiscount: 0, lineTotalTtc: 1.234, lineTaxAmount: 0.197 }],
      payments: [{ id: '00000000-0000-4000-8000-00000000c0e1', method: 'Cash', amount: 1.2, reference: null }],
    };
    const pushed = await (await request(1, '/api/sync/push', 'POST', { deviceCode: 'C01', sales: [roundedSale] }, b.token)).json();
    assert.equal(pushed.accepted, 1, JSON.stringify(pushed));
    const absurd = { ...roundedSale, id: '00000000-0000-4000-8000-00000000c0e2', reference: 'C01-000002', localSeq: 2, roundingAdjustment: -5 };
    assert.equal((await (await request(1, '/api/sync/push', 'POST', { deviceCode: 'C01', sales: [absurd] }, b.token)).json()).rejected, 1);
    const day = soldAtUtc.slice(0, 10);
    const report = await (await request(1, `/api/reports/summary?from=${day}T00:00:00Z&to=${day}T23:59:59Z`, 'GET', undefined, b.token)).json();
    assert.equal(report.roundingTotal, -0.034, 'report keeps cash rounding apart');
    assert.equal(report.grossTtc, 1.234);
    const product = { sku: 'X1', barcode: null, nameAr: '', nameFr: 'X', nameEn: 'X', categoryId: null,
      price: 10, costPrice: 5, taxRate: 13, unit: 'pcs', trackStock: false, isActive: true };
    assert.equal((await request(0, '/api/catalog/products', 'POST', product, a.token)).status, 400, 'rate outside the profile');
    assert.equal((await request(0, '/api/catalog/products', 'POST', { ...product, taxRate: 9 }, a.token)).status, 201);
    const store = await (await request(0, '/api/catalog/store', 'GET', undefined, a.token)).json();
    const storeUpdate = { ...store, fiscalIds: { nif: ' 0001 ', ice: 'x' } };
    assert.equal((await request(0, '/api/catalog/store', 'PUT', storeUpdate, a.token)).status, 400, 'identifier outside the profile');
    const saved = await (await request(0, '/api/catalog/store', 'PUT', { ...storeUpdate, fiscalIds: { nif: ' 0001 ' } }, a.token)).json();
    assert.deepEqual(saved.fiscalIds, { nif: '0001' });
    // Every user works at a branch, whatever the role (BranchLock), so the new user names one. A
    // manager, not a cashier: this plan allows one branch, the main warehouse, which hosts no
    // cashier (2026-10-05) — and the check is about how many users the plan allows, not their role.
    const branches = await (await request(0, '/api/catalog/branches', 'GET', undefined, a.token)).json();
    // Every location carries a read-only three-digit code, 100 to 999, drawn by the server (2026-10-05;
    // three digits since 2026-10-19).
    assert.match(branches[0].code ?? '', /^[1-9]\d{2}$/, 'a branch carries its three-digit code');
    // An account is given to an employee since 2026-10-05, and the server draws its Login ID: each
    // contender is a different employee, with no username of its own. The employee's number is drawn
    // too — Emp-, the location's three digits, YYMM, a sequence — and this company has one location.
    const employee = async (fullName) => {
      const hired = await (await request(0, '/api/hr/employees', 'POST', {
        fullName, branchId: branches[0].id, payBasis: 'Monthly', baseRate: 30000, payoutMethod: 'Cash', hiredOn: '2026-01-01', isActive: true,
      }, a.token)).json();
      assert.match(hired.number ?? '', /^Emp-[1-9]\d{2}\d{4}\d{3,}$/, 'a new hire is given a number the server drew');
      assert.equal(hired.number.slice(4, 7), branches[0].code, "the number carries the company's one location");
      return hired.id;
    };
    const [first, second, third] = [await employee('Cashier'), await employee('Competing'), await employee('Over limit')];
    const user = { role: 'Manager', password, pin: '1234', branchId: branches[0].id };
    const competing = await Promise.all([
      request(0, '/api/users', 'POST', { ...user, employeeId: first }, a.token),
      request(0, '/api/users', 'POST', { ...user, employeeId: second }, a.token),
    ]);
    assert.deepEqual(competing.map(response => response.status).sort(), [201, 409], 'concurrent plan limit');
    assert.equal((await request(0, '/api/users', 'POST', { ...user, employeeId: third }, a.token)).status, 409);
    const branch = { nameAr: '', nameFr: 'Extra', nameEn: 'Extra', address: '', phone: '', isMain: false, allowNegativeStock: false, isActive: true };
    // The plan's limit, said as its code: running out of branch codes (branch_codes_exhausted) is
    // also a 409, and is not what refuses a second location here.
    const overBranches = await request(0, '/api/catalog/branches', 'POST', branch, a.token);
    assert.equal(overBranches.status, 409);
    assert.equal((await overBranches.json()).code, 'subscription_limit', 'the plan limit, not the branch codes');
    assert.equal((await request(0, `/api/users/${a.user.id}`, 'PUT', { fullName: 'Admin', role: 'Cashier', isActive: true }, a.token)).status, 400);
    assert.equal((await request(0, `/api/users/${a.user.id}/revoke-sessions`, 'POST', {}, a.token)).status, 204);
    assert.equal((await request(0, '/api/catalog/store', 'GET', undefined, a.token)).status, 401, 'session revocation');
    console.log('PASS: tenant isolation, manual subscriptions, limits, inactive reads/sync, country profiles, cash rounding, language packs, self-demotion, session revocation.');
  } finally {
    if (server.exitCode === null && server.pid) {
      const stopped = new Promise(resolve => server.once('exit', resolve));
      server.kill();
      await stopped;
    }
    // Only remove the unique directory created by this test under the system temp root.
    if (path.dirname(temp) === path.resolve(tmpdir()) && path.basename(temp).startsWith('algeria-pos-saas-'))
      await rm(temp, { recursive: true, force: true });
  }
})().catch(error => { console.error(error.message); process.exitCode = 1; });

