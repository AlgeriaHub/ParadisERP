// Full OIDC code + PKCE flow against a local test issuer and the real API.
const assert = require('node:assert/strict');
const http = require('node:http');
const { spawn } = require('node:child_process');
const { mkdtemp, mkdir, rm } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const listen = server => new Promise(resolve => server.listen(0, '127.0.0.1', () => resolve(server.address().port)));
const stop = server => new Promise(resolve => server.close(resolve));

(async () => {
  const temp = await mkdtemp(path.join(tmpdir(), 'pos-sso-test-'));
  await mkdir(path.join(temp, 'wwwroot'));
  const { privateKey, publicKey } = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
  const jwk = { ...publicKey.export({ format: 'jwk' }), kid: 'test-key', alg: 'RS256', use: 'sig' };
  let subject = 'operator', issuer, rejectNonce = false;
  const codes = new Map();
  const secret = crypto.randomBytes(24).toString('hex');
  const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
  const idp = http.createServer(async (req, res) => {
    const url = new URL(req.url, issuer);
    res.setHeader('Content-Type', 'application/json');
    if (url.pathname === '/.well-known/openid-configuration') return res.end(JSON.stringify({ issuer,
      authorization_endpoint: issuer + '/authorize', token_endpoint: issuer + '/token', jwks_uri: issuer + '/jwks',
      response_types_supported: ['code'], subject_types_supported: ['public'], id_token_signing_alg_values_supported: ['RS256'],
      token_endpoint_auth_methods_supported: ['client_secret_post'], code_challenge_methods_supported: ['S256'] }));
    if (url.pathname === '/jwks') return res.end(JSON.stringify({ keys: [jwk] }));
    if (url.pathname === '/authorize') {
      assert.equal(url.searchParams.get('code_challenge_method'), 'S256');
      const code = crypto.randomUUID();
      codes.set(code, { subject, params: url.searchParams });
      const redirect = new URL(url.searchParams.get('redirect_uri'));
      redirect.searchParams.set('code', code); redirect.searchParams.set('state', url.searchParams.get('state'));
      res.writeHead(302, { Location: redirect.href }); return res.end();
    }
    if (url.pathname === '/token') {
      let body = ''; for await (const data of req) body += data;
      const form = new URLSearchParams(body), code = codes.get(form.get('code'));
      if (!code || form.get('client_secret') !== secret || form.get('client_id') !== 'pos-test'
          || form.get('redirect_uri') !== code.params.get('redirect_uri')
          || crypto.createHash('sha256').update(form.get('code_verifier') || '').digest('base64url') !== code.params.get('code_challenge')) {
        res.statusCode = 400; return res.end(JSON.stringify({ error: 'invalid_grant' }));
      }
      codes.delete(form.get('code'));
      const now = Math.floor(Date.now() / 1000);
      const payload = { iss: issuer, aud: 'pos-test', sub: code.subject, name: code.subject, iat: now, exp: now + 300,
        nonce: rejectNonce ? 'incorrect-nonce' : code.params.get('nonce') };
      const data = encode({ alg: 'RS256', kid: 'test-key' }) + '.' + encode(payload);
      const idToken = data + '.' + crypto.sign('RSA-SHA256', Buffer.from(data), privateKey).toString('base64url');
      return res.end(JSON.stringify({ id_token: idToken, access_token: 'test-access', token_type: 'Bearer', expires_in: 300 }));
    }
    res.statusCode = 404; res.end('{}');
  });
  issuer = `http://127.0.0.1:${await listen(idp)}`;
  const reservation = http.createServer(); const port = await listen(reservation); await stop(reservation);
  const password = crypto.randomBytes(24).toString('hex');
  // RateLimits off: this script fires its checks back to back from one address (limits have their own tests).
  const env = { ...process.env, RateLimits__Enabled: 'false', ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: `http://127.0.0.1:${port}`,
    Jwt__Key: crypto.randomBytes(32).toString('hex'), Logging__LogLevel__Default: 'Warning', Logging__EventLog__LogLevel__Default: 'None',
    Platform__Enabled: 'true', Platform__Host: 'ops.test', Platform__ConnectionString: `Data Source=${path.join(temp, 'platform.db')}`,
    Platform__OperatorSubjects__0: 'operator', Sso__Enabled: 'true', Sso__Authority: issuer, Sso__ClientId: 'pos-test', Sso__ClientSecret: secret };
  for (let i = 0; i < 2; i++) for (const [key, value] of Object.entries({ Id: `company-${i}`, Hosts__0: `company-${i}.test`,
    ConnectionString: `Data Source=${path.join(temp, `company-${i}.db`)}`, BootstrapAdminPassword: password,
    Subscription__Status: i ? 'Suspended' : 'Active', Subscription__ExpiresAtUtc: '2099-01-01T00:00:00Z' })) env[`Tenancy__Companies__${i}__${key}`] = value;
  const dll = path.resolve(__dirname, `../../ParadiERP.API/bin/${process.env.BUILD_CONFIGURATION || 'Debug'}/net8.0/ParadiERP.API.dll`);
  // A persistent key ring: portal-created companies' connection strings must decrypt after a restart.
  env.DataProtection__KeysPath = path.join(temp, 'keys');
  let child, logs = '', launchError;
  const startApi = () => {
    child = spawn('dotnet', [dll], { cwd: temp, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    child.stdout.on('data', data => { logs = (logs + data).slice(-15000); });
    child.stderr.on('data', data => { logs = (logs + data).slice(-15000); });
    child.on('error', error => { launchError = error; });
  };
  async function waitReady() {
    // Up to 90 s, as saas-api.cjs: fresh databases are migrated through the whole history first.
    for (let i = 0; i < 450; i++) {
      if (launchError) throw launchError;
      if (child.exitCode !== null) throw new Error(logs);
      try { if ((await request('company-0.test', '/api/auth/ping')).status === 200) return; } catch {}
      await new Promise(resolve => setTimeout(resolve, 200));
    }
    assert.fail(logs);
  }
  startApi();
  const jars = new Map();
  async function request(host, route, method = 'GET', body, extra = {}) {
    if (!jars.has(host)) jars.set(host, new Map());
    const jar = jars.get(host);
    return new Promise((resolve, reject) => {
      const req = http.request({ hostname: '127.0.0.1', port, path: route, method,
        headers: { Host: `${host}:${port}`, Cookie: [...jar].map(([key, value]) => `${key}=${value}`).join('; '),
          'Content-Type': 'application/json', ...extra } }, res => {
        for (const cookie of res.headers['set-cookie'] || []) {
          const pair = cookie.split(';')[0], index = pair.indexOf('=');
          const name = pair.slice(0, index), value = pair.slice(index + 1);
          if (value) jar.set(name, value); else jar.delete(name);
        }
        let data = ''; res.on('data', chunk => { data += chunk; });
        res.on('end', () => resolve({ status: res.statusCode, location: res.headers.location, text: data, json: () => JSON.parse(data) }));
      });
      req.on('error', reject); req.setTimeout(10000, () => req.destroy(new Error('HTTP timeout')));
      req.end(body === undefined ? undefined : JSON.stringify(body));
    });
  }
  async function login(host, identity) {
    subject = identity;
    const start = await request(host, '/api/auth/sso/login');
    assert.equal(start.status, 302, start.text);
    const authorize = await fetch(start.location, { redirect: 'manual' });
    const callback = new URL(authorize.headers.get('location'));
    return request(host, callback.pathname + callback.search);
  }
  try {
    await waitReady();
    assert.equal((await request('ops.test', '/api/platform/companies')).status, 401);
    await login('ops.test', 'ordinary-user');
    assert.equal((await request('ops.test', '/api/platform/companies')).status, 403, 'ordinary identity cannot operate platform');
    jars.delete('ops.test');
    assert.equal((await login('ops.test', 'operator')).location, '/#/platform');
    const session = await request('ops.test', '/api/platform/session');
    assert.equal(session.status, 200, session.text);
    const headers = { 'X-CSRF-TOKEN': session.json().csrfToken };
    const companies = (await request('ops.test', '/api/platform/companies')).json();
    const company = companies.find(c => c.id === 'company-1');
    const change = { settings: { ...company.settings, status: 'Active', plan: 'Renewed' }, revision: company.revision, reason: 'Payment received' };
    assert.equal((await request('ops.test', '/api/platform/companies/company-1/subscription', 'PUT', change)).status, 400, 'CSRF protection');
    assert.equal((await request('ops.test', '/api/platform/companies/company-1/subscription', 'PUT', change, headers)).status, 204);
    assert.equal((await request('ops.test', '/api/platform/companies/company-1/subscription', 'PUT', change, headers)).status, 409, 'stale edit');
    const localLogin = (await request('company-1.test', '/api/auth/login', 'POST', { username: 'admin', password, deviceCode: 'C01' })).json();
    assert.equal(localLogin.subscription.canOperate, true, 'renewal is immediately effective');
    const presets = (await request('ops.test', '/api/platform/regional-presets')).json();
    assert.equal(presets.MA.currencyCode, 'MAD');
    assert.equal(company.regional.currencyCode, 'DZD', 'configured default profile');
    const regional = { settings: presets.MA, revision: company.regionalRevision, reason: 'Company moved to Morocco' };
    const badRegional = { ...regional, settings: { ...presets.MA, defaultTaxRate: 19 } };
    assert.equal((await request('ops.test', '/api/platform/companies/company-1/regional', 'PUT', badRegional, headers)).status, 400, 'profile validation');
    assert.equal((await request('ops.test', '/api/platform/companies/company-1/regional', 'PUT', regional, headers)).status, 204);
    assert.equal((await request('ops.test', '/api/platform/companies/company-1/regional', 'PUT', regional, headers)).status, 409, 'stale profile edit');
    const movedLogin = (await request('company-1.test', '/api/auth/login', 'POST', { username: 'admin', password, deviceCode: 'C01' })).json();
    assert.equal(movedLogin.regional.currencyCode, 'MAD', 'profile change is immediately effective');
    const withBerber = { settings: { ...presets.MA, languages: ['fr', 'tzm'] }, revision: 1, reason: 'Add Tamazight' };
    assert.equal((await request('ops.test', '/api/platform/companies/company-1/regional', 'PUT', withBerber, headers)).status, 400, 'language needs a pack');
    const pack = { code: 'tzm', label: 'Tamaziɣt', rtl: false, fallbackLanguage: 'fr', strings: { 'login.title': 'Tuqqna' } };
    assert.equal((await request('ops.test', '/api/platform/language-packs/tzm', 'PUT', { pack, revision: 0, reason: 'New market' }, headers)).status, 204);
    assert.equal((await request('ops.test', '/api/platform/language-packs/tzm', 'PUT', { pack, revision: 0, reason: 'Stale' }, headers)).status, 409, 'stale pack edit');
    assert.equal((await request('ops.test', '/api/platform/language-packs/fr', 'PUT', { pack: { ...pack, code: 'fr' }, revision: 0, reason: 'x' }, headers)).status, 400, 'built-in languages are protected');
    assert.equal((await request('ops.test', '/api/platform/companies/company-1/regional', 'PUT', withBerber, headers)).status, 204);
    const packs = (await request('ops.test', '/api/platform/language-packs')).json();
    assert.deepEqual(packs.map(p => [p.code, p.strings, p.revision]), [['tzm', 1, 1]]);
    assert.equal((await request('ops.test', '/api/platform/language-packs/tzm', 'DELETE', undefined, headers)).status, 409, 'pack in use');
    const berberLogin = (await request('company-1.test', '/api/auth/login', 'POST', { username: 'admin', password, deviceCode: 'C01' })).json();
    assert.equal(berberLogin.languagePacks.tzm, 1);
    assert.equal((await request('company-1.test', '/api/i18n/tzm')).json().label, 'Tamaziɣt');
    const audit = (await request('ops.test', '/api/platform/companies/company-1/audit')).json();
    assert.equal(audit.length, 3); assert.equal(audit[0].action, 'regional.updated'); assert.equal(audit[2].actorSubject, 'operator');
    assert.equal((await request('company-1.test', '/api/platform/companies', 'GET', undefined, { Authorization: `Bearer ${localLogin.token}` })).status, 404);

    // ---- companies created in the portal, live ----
    const trial = { plan: 'Trial', status: 'Trial', expiresAtUtc: '2099-01-01T00:00:00Z', graceDays: 7, maxUsers: null, maxBranches: null, maxDevices: null };
    const newCompany = { id: 'live-shop', hosts: ['live.test'], databaseProvider: 'Sqlite', connectionString: null,
      country: 'TN', adminUsername: 'owner', subscription: trial, reason: 'Signed contract' };
    assert.equal((await request('ops.test', '/api/platform/companies', 'POST', newCompany)).status, 400, 'CSRF on create');
    assert.equal((await request('live.test', '/api/auth/ping')).status, 404, 'unknown before creation');
    const createdResponse = await request('ops.test', '/api/platform/companies', 'POST', newCompany, headers);
    assert.equal(createdResponse.status, 201, createdResponse.text);
    const created = createdResponse.json();
    assert.equal(created.bootstrapPassword.length, 20);
    const liveLogin = await request('live.test', '/api/auth/login', 'POST', { username: 'owner', password: created.bootstrapPassword, deviceCode: 'C01' });
    assert.equal(liveLogin.status, 200, liveLogin.text);
    assert.equal(liveLogin.json().regional.currencyCode, 'TND', 'chosen country preset');
    assert.equal(liveLogin.json().subscription.status, 'Trial', 'chosen subscription');
    const createAgain = (body) => request('ops.test', '/api/platform/companies', 'POST', { ...newCompany, ...body }, headers);
    assert.equal((await createAgain({ id: 'other-shop', hosts: ['company-0.test'] })).status, 400, 'hostname collision');
    assert.equal((await createAgain({ id: 'other-shop', hosts: ['ops.test'] })).status, 400, 'operator hostname reserved');
    assert.equal((await createAgain({ id: 'Bad Id', hosts: ['bad.test'] })).status, 400, 'invalid id');
    assert.equal((await createAgain({ hosts: ['again.test'] })).status, 409, 'duplicate id');
    const companyList = await request('ops.test', '/api/platform/companies');
    const listed = companyList.json().find(c => c.id === 'live-shop');
    assert.deepEqual([listed.source, listed.state, listed.companyRevision], ['Portal', 'Active', 1]);
    assert.ok(!companyList.text.includes(created.bootstrapPassword) && !companyList.text.includes('live-shop.db'), 'no secrets in the list');
    const companyAction = (id, action, body) => request('ops.test', `/api/platform/companies/${id}/${action}`, action === 'hosts' ? 'PUT' : 'POST', body, headers);
    assert.equal((await companyAction('company-0', 'archive', { revision: 0, reason: 'No' })).status, 400, 'configured companies are read-only');
    assert.equal((await companyAction('live-shop', 'archive', { revision: 1, reason: 'Unpaid' })).status, 204);
    assert.equal((await request('live.test', '/api/auth/ping')).status, 404, 'archived company is unreachable');
    assert.equal((await companyAction('live-shop', 'restore', { revision: 1, reason: 'Stale' })).status, 409, 'stale restore');
    assert.equal((await companyAction('live-shop', 'restore', { revision: 2, reason: 'Paid' })).status, 204);
    assert.equal((await request('live.test', '/api/auth/ping')).status, 200, 'restored company is reachable');
    assert.equal((await companyAction('live-shop', 'hosts', { hosts: ['shop.live.test'], revision: 3, reason: 'New domain' })).status, 204);
    assert.equal((await request('live.test', '/api/auth/ping')).status, 404, 'old hostname released');
    assert.equal((await request('shop.live.test', '/api/auth/ping')).status, 200, 'new hostname is live');
    const liveAudit = await request('ops.test', '/api/platform/companies/live-shop/audit');
    assert.ok(!liveAudit.text.includes(created.bootstrapPassword) && !liveAudit.text.includes('Data Source'), 'no secrets in audit');
    assert.deepEqual(liveAudit.json().map(a => a.action).sort(), ['company.archived', 'company.created', 'company.hosts', 'company.restored']);

    // The company, its encrypted connection string and the operator session all survive a restart.
    const exited = new Promise(resolve => child.once('exit', resolve)); child.kill(); await exited;
    startApi(); await waitReady();
    assert.equal((await request('shop.live.test', '/api/auth/login', 'POST',
      { username: 'owner', password: created.bootstrapPassword, deviceCode: 'C01' })).status, 200, 'portal company after restart');
    assert.equal((await request('ops.test', '/api/platform/companies')).status, 200, 'operator session after restart');
    await login('company-0.test', 'owner');
    assert.equal((await request('company-0.test', '/api/auth/sso/session?deviceCode=C01')).status, 403, 'unlinked identity');
    const users = (await request('ops.test', '/api/platform/companies/company-0/users')).json();
    const linked = await request('ops.test', '/api/platform/companies/company-0/identities', 'POST', { subject: 'owner', userId: users[0].id }, headers);
    assert.equal(linked.status, 201, linked.text);
    const posSession = await request('company-0.test', '/api/auth/sso/session?deviceCode=C01');
    assert.equal(posSession.status, 200, posSession.text); assert.equal(posSession.json().user.id, users[0].id);
    jars.set('company-1.test', new Map(jars.get('company-0.test')));
    assert.equal((await request('company-1.test', '/api/auth/sso/session?deviceCode=C01')).status, 401, 'cookie hostname binding');
    assert.equal((await request('ops.test', `/api/platform/companies/company-0/identities/${linked.json().id}`, 'DELETE', undefined, headers)).status, 204);
    assert.equal((await request('company-0.test', '/api/auth/sso/session?deviceCode=C01')).status, 403, 'unlinked identity denied');
    jars.delete('company-1.test'); rejectNonce = true;
    assert.equal((await login('company-1.test', 'owner')).location, '/?sso=failed#/login', 'nonce validation');
    console.log('PASS: OIDC code/PKCE/nonce, operator isolation, CSRF, durable renewal/audit, revision conflicts, explicit company SSO links, live company provisioning.');
  } finally {
    if (child.exitCode === null && child.pid) { const stopped = new Promise(resolve => child.once('exit', resolve)); child.kill(); await stopped; }
    await stop(idp);
    if (path.dirname(temp) === path.resolve(tmpdir()) && path.basename(temp).startsWith('pos-sso-test-')) await rm(temp, { recursive: true, force: true });
  }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
