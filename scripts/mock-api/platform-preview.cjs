// Local visual QA fixture only; never mount this server in a deployment.
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../../AlgeriaPos.WebUI/dist/algeria-pos-web-ui/browser');
const text = (ar, fr, en) => ({ ar, fr, en });
const algeria = () => ({
  countryCode: 'DZ', currencyCode: 'DZD', currencyDecimals: 2, currencySymbols: text('دج', 'DA', 'DA'), currencySymbolFirst: false,
  timeZone: 'Africa/Algiers', languages: ['fr', 'ar', 'en'], defaultLanguage: 'fr',
  taxNames: text('الرسم على القيمة المضافة', 'TVA', 'VAT'), taxRates: [19, 9, 0], defaultTaxRate: 19, pricesIncludeTax: true,
  taxRateLabels: [{ rate: 19, labels: text('النسبة العادية', 'Taux normal', 'Standard rate') }, { rate: 9, labels: text('النسبة المخفضة', 'Taux réduit', 'Reduced rate') }],
  cashRoundingIncrement: 0, cashRoundingMode: 'Nearest', receiptLegalText: {},
  receiptSections: ['header', 'fiscalIds', 'meta', 'lines', 'totals', 'taxBreakdown', 'payment', 'legal', 'footer'],
  fiscalIds: [
    { key: 'nif', code: 'NIF', labels: text('الرقم الجبائي', 'NIF', 'Tax ID (NIF)'), required: false, showOnReceipt: true },
    { key: 'rc', code: 'RC', labels: text('السجل التجاري', 'RC', 'Trade register (RC)'), required: false, showOnReceipt: true },
  ],
  customerTaxIdLabels: text('الرقم الجبائي', 'NIF', 'Tax ID (NIF)'), cashDenominations: [200, 500, 1000, 2000], paymentMethods: ['Cash', 'Card', 'Credit'],
});
const tunisia = () => ({
  ...algeria(), countryCode: 'TN', currencyCode: 'TND', currencyDecimals: 3, currencySymbols: text('د.ت', 'DT', 'TND'),
  timeZone: 'Africa/Tunis', taxRates: [19, 13, 7, 0], fiscalIds: [{ key: 'mf', code: 'MF', labels: text('المعرف الجبائي', 'Matricule fiscal', 'Tax ID'), required: false, showOnReceipt: true }],
  cashDenominations: [5, 10, 20, 50],
});
function createPreviewServer() {
const companies = [
  { id: 'alger-centre', hosts: ['alger.pos.example.com'], revision: 1, settings: { plan: 'Standard', status: 'Active', expiresAtUtc: '2026-12-01T00:00:00Z', graceDays: 7, maxUsers: 10, maxBranches: 3, maxDevices: 5 }, access: { status: 'Active', canOperate: true }, regional: algeria(), regionalRevision: 0, source: 'Configuration', state: 'Active', companyRevision: 0 },
  { id: 'oran-commerce', hosts: ['oran.pos.example.com'], revision: 0, settings: { plan: 'Starter', status: 'Trial', expiresAtUtc: '2026-10-01T00:00:00Z', graceDays: 7, maxUsers: 3, maxBranches: 1, maxDevices: 2 }, access: { status: 'Trial', canOperate: true }, regional: algeria(), regionalRevision: 0, source: 'Portal', state: 'Active', companyRevision: 1 },
];
const packs = [];
const audit = [{ id: '1', actorSubject: 'operator', action: 'subscription.updated', detail: JSON.stringify({ reason: 'Annual subscription activated' }), createdAtUtc: '2026-09-16T10:00:00Z' }];
return http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  function json(value, status = 200) { res.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }); res.end(JSON.stringify(value)); }
  if (url.pathname === '/api/auth/ping') return json({ ok: true });
  if (url.pathname === '/api/platform/session') return json({ name: 'Platform operator', csrfToken: 'preview-only' });
  if (url.pathname === '/api/platform/companies' && req.method === 'POST') {
    let body = ''; for await (const chunk of req) body += chunk;
    const input = JSON.parse(body);
    if (companies.some(c => c.id === input.id)) return json(`A company with ID '${input.id}' already exists.`, 409);
    companies.push({ id: input.id, hosts: input.hosts, revision: 1, settings: input.subscription,
      access: { status: input.subscription.status, canOperate: true }, regional: input.country === 'TN' ? tunisia() : algeria(),
      regionalRevision: 0, source: 'Portal', state: 'Active', companyRevision: 1 });
    audit.unshift({ id: String(Date.now()), actorSubject: 'operator', action: 'company.created', detail: JSON.stringify({ reason: input.reason }), createdAtUtc: new Date().toISOString() });
    return json({ id: input.id, adminUsername: input.adminUsername, bootstrapPassword: 'Preview-one-time-01' }, 201);
  }
  const companyMatch = url.pathname.match(/^\/api\/platform\/companies\/([a-z0-9-]+)\/(hosts|archive|restore)$/);
  if (companyMatch) {
    let body = ''; for await (const chunk of req) body += chunk;
    const input = JSON.parse(body), company = companies.find(c => c.id === companyMatch[1]);
    if (!company) return json({}, 404);
    if (company.source !== 'Portal') return json('This company is defined in server configuration; change it there.', 400);
    if (input.revision !== company.companyRevision) return json('Another operator changed this company. Reload before saving.', 409);
    if (companyMatch[2] === 'hosts') company.hosts = input.hosts; else company.state = companyMatch[2] === 'archive' ? 'Archived' : 'Active';
    company.companyRevision++;
    audit.unshift({ id: String(Date.now()), actorSubject: 'operator', action: 'company.' + companyMatch[2], detail: JSON.stringify({ reason: input.reason }), createdAtUtc: new Date().toISOString() });
    res.writeHead(204); return res.end();
  }
  if (url.pathname === '/api/platform/companies') return json(companies);
  const integrationsMatch = url.pathname.match(/^\/api\/platform\/companies\/([^/]+)\/integrations(\/test-webhook)?$/);
  if (integrationsMatch) {
    const company = companies.find(c => c.id === integrationsMatch[1]);
    if (!company) return json({}, 404);
    company.integrations ??= { settings: { card: { mode: 'None', requireCardReference: false },
      sms: { enabled: false, url: '', method: 'POST', tokenHeader: 'Authorization', contentType: 'application/json', bodyTemplate: '{"to":"{to}","message":"{message}"}' },
      email: { enabled: false, host: '', port: 587, useTls: true, user: '', from: '' },
      webhook: { enabled: false, url: '' } }, revision: 0, hasSmsToken: false, hasSmtpPassword: false, hasWebhookSecret: false,
      outbox: { pending: 0, failing: 0, lastError: null } };
    if (integrationsMatch[2]) return json({ ok: true, error: null });
    if (req.method === 'GET') return json(company.integrations);
    let body = ''; for await (const chunk of req) body += chunk;
    const input = JSON.parse(body);
    if (input.revision !== company.integrations.revision) return json('Another operator changed these integrations.', 409);
    company.integrations = { ...company.integrations, settings: input.settings, revision: input.revision + 1,
      hasSmsToken: company.integrations.hasSmsToken || !!input.secrets?.smsToken,
      hasWebhookSecret: company.integrations.hasWebhookSecret || !!input.secrets?.webhookSecret };
    audit.unshift({ id: String(Date.now()), actorSubject: 'operator', action: 'integrations.updated', detail: JSON.stringify({ reason: input.reason }), createdAtUtc: new Date().toISOString() });
    res.writeHead(204); return res.end();
  }
  if (url.pathname === '/api/platform/regional-presets') return json({ DZ: algeria(), TN: tunisia() });
  if (url.pathname === '/api/platform/language-packs') return json(packs.map(p => ({ code: p.pack.code, label: p.pack.label, rtl: p.pack.rtl,
    fallbackLanguage: p.pack.fallbackLanguage, strings: Object.keys(p.pack.strings).length, revision: p.revision })));
  const packMatch = url.pathname.match(/^\/api\/platform\/language-packs\/([a-z-]+)$/);
  if (packMatch) {
    const index = packs.findIndex(p => p.pack.code === packMatch[1]);
    if (req.method === 'GET') return index < 0 ? json({}, 404) : json(packs[index].pack);
    if (req.method === 'DELETE') {
      if (companies.some(c => c.regional.languages.includes(packMatch[1]))) return json('A company still uses this language.', 409);
      if (index >= 0) packs.splice(index, 1);
      res.writeHead(204); return res.end();
    }
    let body = ''; for await (const chunk of req) body += chunk;
    const input = JSON.parse(body);
    if (input.revision !== (index < 0 ? 0 : packs[index].revision)) return json('Stale language pack.', 409);
    if (index < 0) packs.push({ pack: input.pack, revision: 1 }); else packs[index] = { pack: input.pack, revision: input.revision + 1 };
    res.writeHead(204); return res.end();
  }
  if (url.pathname.endsWith('/regional') && req.method === 'PUT') {
    let body = ''; for await (const chunk of req) body += chunk;
    const input = JSON.parse(body), id = url.pathname.split('/')[4], company = companies.find(c => c.id === id);
    if (!company) return json({}, 404);
    if (input.revision !== company.regionalRevision) return json('Another operator changed this country profile. Reload before saving.', 409);
    company.regional = input.settings; company.regionalRevision++;
    audit.unshift({ id: String(Date.now()), actorSubject: 'operator', action: 'regional.updated', detail: JSON.stringify({ reason: input.reason }), createdAtUtc: new Date().toISOString() });
    res.writeHead(204); return res.end();
  }
  if (url.pathname.endsWith('/users')) return json([{ id: 'owner', username: 'owner', fullName: 'Company owner', role: 'Admin', isActive: true }]);
  if (url.pathname.endsWith('/audit')) return json(audit);
  if (url.pathname.endsWith('/identities')) return json([]);
  if (url.pathname.endsWith('/subscription') && req.method === 'PUT') {
    let body = ''; for await (const chunk of req) body += chunk;
    const input = JSON.parse(body), id = url.pathname.split('/')[4], company = companies.find(c => c.id === id);
    if (!company) return json({}, 404);
    company.settings = input.settings; company.revision++;
    audit.unshift({ id: String(Date.now()), actorSubject: 'operator', action: 'subscription.updated', detail: JSON.stringify({ reason: input.reason }), createdAtUtc: new Date().toISOString() });
    res.writeHead(204); return res.end();
  }
  if (url.pathname.startsWith('/api/')) return json({}, 404);
  const target = path.resolve(root, '.' + decodeURIComponent(url.pathname));
  if (target !== root && !target.startsWith(root + path.sep)) { res.writeHead(404); return res.end(); }
  const file = fs.existsSync(target) && fs.statSync(target).isFile() ? target : path.join(root, 'index.html');
  const mime = { '.js': 'text/javascript', '.css': 'text/css', '.html': 'text/html', '.json': 'application/json', '.png': 'image/png', '.ico': 'image/x-icon' };
  res.writeHead(200, { 'Content-Type': mime[path.extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-store' });
  fs.createReadStream(file).pipe(res);
});
}
module.exports = { createPreviewServer };
if (require.main === module) {
  createPreviewServer().listen(5097, '127.0.0.1', () => console.log('Platform visual fixture: http://127.0.0.1:5097/#/platform'));
}
