const { requiredPassword } = require('./test-credentials.cjs');
// Drives every printed document through the live /print route and measures the paper
// (docs/07 §4.4).
//
// A document that renders is not a document that prints. The page size of each kind is injected
// at runtime by PrintPageComponent, because an @page rule cannot be scoped to a selector — so the
// only way to know a voucher really comes out on A5 and the ageing board really comes out
// landscape is to print one and measure the MediaBox. That is what this does: it opens each of
// the nine kinds in both French and Arabic, asks Chromium for a PDF with preferCSSPageSize, and
// checks the sheet size, the number of sheets, the direction of the document and that not one
// figure reached the paper in Arabic-Indic digits.
//
// Start the API against a database with real documents in it (the provider-smoke recipe leaves
// exactly that behind), and run:
//   BASE=http://127.0.0.1:5125 node tests/e2e/print-documents.cjs
// PLAYWRIGHT_CHANNEL=chrome selects an installed Chrome when the bundled Chromium is missing.
// The PDFs land in artifacts/print/ (gitignored), so a failed size check can be looked at.
//
// A kind with no document in the database is SKIPped, not failed: this asserts what the paper
// does, not what the seed contains.
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');

const BASE = process.env.BASE || 'http://127.0.0.1:5125';
const OUT = process.env.OUT || path.join(process.cwd(), 'artifacts', 'print');
const USER = process.env.POS_USER || 'admin';
const PASSWORD = requiredPassword('POS_PASSWORD');
const DEVICE = process.env.POS_DEVICE || 'C01';

// A4 and A5 as Chromium lays them out, in points. The plan's figures; 1 pt of slack absorbs the
// rounding between 210 mm and 595.28 pt without letting A4 pass for A5.
const A4 = { width: 594.96, height: 841.92 };
const A4_LANDSCAPE = { width: A4.height, height: A4.width };
const A5 = { width: 420, height: 594.96 };
const TOLERANCE = 1;

let failures = 0;
let skipped = 0;
const check = (name, ok, extra = '') => {
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${extra ? ' — ' + extra : ''}`);
  if (!ok) failures++;
};
const skip = (name, why) => {
  console.log(`SKIP  ${name} — ${why}`);
  skipped++;
};

async function json(route, token) {
  const res = await fetch(BASE + route, {
    headers: { 'content-type': 'application/json', ...(token ? { authorization: 'Bearer ' + token } : {}) },
  });
  const text = await res.text();
  try {
    return { status: res.status, body: text ? JSON.parse(text) : null };
  } catch {
    return { status: res.status, body: text };
  }
}

/**
 * The sheets of a PDF, as Chromium wrote it: one /MediaBox per page object, in points. No
 * library — the boxes are plain text in the object stream and a parser here would be one more
 * thing to be wrong.
 */
function sheetsOf(buffer) {
  const text = buffer.toString('latin1');
  const boxes = [...text.matchAll(/\/MediaBox\s*\[\s*([\d.+-]+)\s+([\d.+-]+)\s+([\d.+-]+)\s+([\d.+-]+)\s*\]/g)];
  return boxes.map((m) => ({ width: Number(m[3]) - Number(m[1]), height: Number(m[4]) - Number(m[2]) }));
}

const near = (a, b) => Math.abs(a - b) <= TOLERANCE;

/** The query a kind needs beyond its id, as PrintService builds it. */
function urlFor(kind, id, lang, extra = {}) {
  const params = new URLSearchParams({ lang, ...extra });
  return `${BASE}/#/print/${kind}/${encodeURIComponent(id)}?${params}`;
}

(async () => {
  const login = await fetch(BASE + '/api/auth/login', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ username: USER, password: PASSWORD, deviceCode: DEVICE }),
  });
  const token = login.ok ? (await login.json()).token : null;
  check('admin signs in', !!token, 'HTTP ' + login.status);
  if (!token) {
    console.log('\nno token, stopping');
    process.exit(1);
  }

  // The ids to print. Each is looked up the way a screen would, so a kind with nothing behind it
  // is simply skipped rather than printing an empty frame and calling it a pass.
  const invoices = (await json('/api/receivables/invoices?take=50', token)).body || [];
  const creditNotes = (await json('/api/receivables/credit-notes?take=50', token)).body || [];
  const receipts = (await json('/api/receivables/receipts?take=50', token)).body || [];
  const contracts = (await json('/api/rentals/contracts', token)).body || [];

  let contractId = null;
  let issueNoteId = null;
  let returnNoteId = null;
  for (const row of contracts) {
    const issues = (await json(`/api/rentals/contracts/${row.id}/issue-notes`, token)).body || [];
    if (!issues.length) continue;
    const returns = (await json(`/api/rentals/contracts/${row.id}/return-notes`, token)).body || [];
    // Prefer a contract that has been through the whole cycle, so all three rental kinds print.
    if (!contractId || (returns.length && !returnNoteId)) {
      contractId = row.id;
      issueNoteId = issues[0].id;
      returnNoteId = returns[0]?.id ?? returnNoteId;
    }
    if (returnNoteId) break;
  }

  // The payslip is the ninth document and the only one printed once per person, so one wasted
  // sheet is one per head on payday. The id is the run and the `line` query narrows it to one
  // employee, which is what makes "one sheet" a figure this can assert at all.
  const runs = (await json('/api/hr/payroll/runs?take=50', token)).body || [];
  let payslipRunId = null;
  let payslipLineId = null;
  for (const run of runs) {
    if (!run.headCount) continue;
    const page = (await json(`/api/hr/payroll/runs/${run.id}/lines?take=1`, token)).body;
    const line = page?.lines?.[0];
    if (!line) continue;
    payslipRunId = run.id;
    payslipLineId = line.id;
    break;
  }

  // The back office's six reports (print-reports.ts), over this year to date: the ledger's window is
  // `from` inclusive and `to` exclusive, and the server refuses one wider than 400 days.
  const yearStart = new Date(Date.UTC(new Date().getUTCFullYear(), 0, 1)).toISOString();
  const tomorrow = new Date(Date.now() + 86400000).toISOString().slice(0, 10) + 'T00:00:00.000Z';
  const trial = (await json(`/api/ledger/reports/trial-balance?from=${yearStart}&to=${tomorrow}`, token)).body;
  // The statement wants an account that moved; the trial balance lists exactly those.
  const movedAccount = trial?.rows?.[0]?.accountId ?? null;
  const period = { from: yearStart, to: tomorrow };

  const statementCustomer = invoices[0]?.customerId ?? null;
  const documents = [
    { kind: 'invoice', id: invoices[0]?.id, size: A4, sheets: (n) => n >= 1 },
    { kind: 'credit-note', id: creditNotes[0]?.id, size: A4, sheets: (n) => n >= 1 },
    // Two copies by default — the company keeps one, the customer takes one.
    { kind: 'receipt-voucher', id: receipts[0]?.id, size: A5, sheets: (n) => n === 2 },
    { kind: 'statement', id: statementCustomer, size: A4, sheets: (n) => n >= 1, table: true },
    { kind: 'ageing', id: '-', size: A4_LANDSCAPE, sheets: (n) => n >= 1, table: true },
    // One copy by default since 2026-10-10 (the owner); the copies box asks for a second.
    { kind: 'rental-contract', id: contractId, size: A4, sheets: (n) => n >= 1 },
    { kind: 'issue-note', id: issueNoteId, size: A4, sheets: (n) => n >= 1, extra: { contract: contractId } },
    { kind: 'return-note', id: returnNoteId, size: A4, sheets: (n) => n >= 1, extra: { contract: contractId } },
    // Exactly one. Two is the failure this pins: the sheet ran about 23 px past the A4 content
    // box and every employee cost a second page carrying little but the repeated footer strip.
    {
      kind: 'payslip',
      id: payslipRunId,
      size: A4,
      sheets: (n) => n === 1,
      extra: payslipLineId ? { line: payslipLineId } : {},
    },
    // Reports: the id is the report's name. Each states its period where a document has its
    // number, and its table head repeats on every sheet.
    { kind: 'report', id: 'trial-balance', size: A4, sheets: (n) => n >= 1, table: true, report: true, extra: period },
    { kind: 'report', id: 'profit-loss', size: A4, sheets: (n) => n >= 1, table: true, report: true, extra: period },
    { kind: 'report', id: 'balance-sheet', size: A4, sheets: (n) => n >= 1, table: true, report: true, extra: { to: tomorrow } },
    {
      kind: 'report',
      id: 'account-statement',
      size: A4,
      sheets: (n) => n >= 1,
      table: true,
      report: true,
      extra: { ...period, account: movedAccount },
    },
    { kind: 'report', id: 'journal', size: A4, sheets: (n) => n >= 1, table: true, report: true, extra: period },
    {
      kind: 'report',
      id: 'sales',
      size: A4,
      sheets: (n) => n >= 1,
      table: true,
      report: true,
      // The sales summary refuses more than 92 days; a month is what a clerk prints.
      extra: { from: new Date(Date.now() - 30 * 86400000).toISOString(), to: new Date().toISOString() },
    },
  ];

  fs.mkdirSync(OUT, { recursive: true });
  const browser = await chromium.launch({
    channel: process.env.PLAYWRIGHT_CHANNEL || undefined,
    args: ['--no-sandbox', '--disable-dev-shm-usage'],
  });
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });

  // One sign-in for the whole run: the print route reads the session out of IndexedDB, which is
  // the context's, not the page's.
  const entry = await context.newPage();
  try {
    await entry.goto(BASE, { waitUntil: 'networkidle' });
    await entry.waitForSelector('.login-wrap', { timeout: 30000 });
    await entry.fill('#server', BASE);
    await entry.fill('#username', USER);
    await entry.fill('#password', PASSWORD);
    if (await entry.locator('#device').count() === 0 && await entry.locator('.shared-toggle').count()) await entry.click('.shared-toggle');
    await entry.fill('#device', DEVICE).catch(() => {});
    await entry.click('button[type=submit]');
    await entry.waitForSelector('.register', { timeout: 30000 });
    check('the session is open in the browser', true);
  } catch (e) {
    check('the session is open in the browser', false, e.message.split('\n')[0]);
    await entry.close();
    await context.close();
    await browser.close();
    console.log('\nno browser session, stopping');
    process.exit(1);
  }
  await entry.close();

  for (const doc of documents) {
    if (!doc.id || (doc.extra && Object.values(doc.extra).some((v) => !v))) {
      skip(`${doc.report ? doc.id : doc.kind}`, 'no such document in this database');
      continue;
    }
    for (const lang of ['fr', 'ar']) {
      const label = doc.report ? doc.id : doc.kind;
      const name = `${label} (${lang})`;
      // A fresh page per document: the print route is one route, so the router would reuse the
      // component across a hash change and never re-read the kind or re-inject its @page.
      const page = await context.newPage();
      const errors = [];
      page.on('pageerror', (e) => errors.push(e.message.split('\n')[0]));
      try {
        await page.goto(urlFor(doc.kind, doc.id, lang, doc.extra), { waitUntil: 'networkidle' });
        await page.waitForSelector('.print-page', { timeout: 30000 });

        const dir = await page.getAttribute('.print-page', 'dir');
        check(`${name} is ${lang === 'ar' ? 'right-to-left' : 'left-to-right'}`, dir === (lang === 'ar' ? 'rtl' : 'ltr'), 'dir ' + dir);

        const text = await page.textContent('.print-page');
        const arabicIndic = /[٠-٩۰-۹]/.test(text || '');
        check(`${name} writes every figure in Latin digits`, !arabicIndic);

        if (doc.report) {
          // The screen print never said what it covered; the sheet must, where an invoice has its number.
          const period = ((await page.textContent('.print-title__number')) || '').trim();
          check(`${name} states its period`, /\d/.test(period), period);
          check(`${name} carries no screen controls`, (await page.locator('.print-sheet button').count()) === 0);
        }

        await page.emulateMedia({ media: 'print' });
        if (doc.table) {
          // A long report runs over the sheet; its head has to come back at the top of page two.
          const display = await page.$eval('.print-table thead', (el) => getComputedStyle(el).display).catch(() => null);
          check(`${name} repeats the table head on every sheet`, display === 'table-header-group', String(display));
        }

        const file = path.join(OUT, `${label}-${lang}.pdf`);
        const buffer = await page.pdf({ path: file, preferCSSPageSize: true, printBackground: true });
        const sheets = sheetsOf(buffer);
        const first = sheets[0];
        check(
          `${name} prints on ${doc.size === A5 ? 'A5' : doc.size === A4_LANDSCAPE ? 'A4 landscape' : 'A4'}`,
          !!first && near(first.width, doc.size.width) && near(first.height, doc.size.height),
          first ? `${first.width.toFixed(2)} × ${first.height.toFixed(2)} pt` : 'no MediaBox',
        );
        check(`${name} comes out on the expected number of sheets`, doc.sheets(sheets.length), 'sheets ' + sheets.length);
        check(`${name} renders without a script error`, errors.length === 0, errors[0] || '');
      } catch (e) {
        check(name, false, e.message.split('\n')[0]);
      }
      await page.close();
    }
  }

  await context.close();
  await browser.close();

  console.log(`\n${failures === 0 ? 'all checks passed' : failures + ' check(s) failed'}${skipped ? `, ${skipped} skipped` : ''}`);
  process.exit(failures === 0 ? 0 : 1);
})();
