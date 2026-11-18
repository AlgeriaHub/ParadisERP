const { requiredPassword } = require('./test-credentials.cjs');
// Exercises phases 1 to 5 against an API already running on a real database provider.
//
// The point is that a schema existing is not the same as the code working. SQLite stores
// decimal as TEXT and MySQL does not, and some routes run inside a transaction opened by an
// endpoint filter while others do not — both are places where a provider or a wiring
// difference surfaces only when the endpoint is actually called.
//
// The first run of this against SQL Server found exactly that: every route that opened its
// own transaction failed outright, because the subscription filter already had one open.
// (SQL Server was removed as a provider on 2026-09-23 — the providers are MySQL in production
// and SQLite for development and tests — but the bug was never provider-specific.)
//
// Start the API against MySQL, the production provider (SQLite is covered by the unit suite),
// then:
//   PROVIDER=MySQL BASE=http://127.0.0.1:5125 node tests/e2e/provider-smoke.cjs
// It is safe to re-run: the tender it creates is uniquely named each time, the per-item
// multiplier it sets is cleared again, and the company's document texts are put back.
//
// 2026-09-25 additions (docs/07 §5): a month of wages end to end — an employee, a declared
// sheet, a run computed and approved, the accrual balanced in the books, an advance recovered
// by the next run, and the wages paid out of the branch's box. It takes two consecutive months
// of its own, picked by reading which are free, so re-running it never collides with itself.
//
// 2026-09-24 additions (docs/07 §3 and §4): contract rows carry their own quantities and money,
// a per-item overdue multiplier reads back, a signed overdue price under the company floor is
// refused, a credit note reads by id, a new invoice freezes the buyer, and the printed-document
// texts are written without bumping the replicated store row.
// 2026-09-30 additions (the owner's paperwork answers): the rental flow meets the new rules —
// a person renter whose five identity fields are on the customer record, a site added through
// /api/customers/{id}/sites on every contract, and the driver and truck on both notes — and it
// checks that a contract without a site and an issue note without a driver are refused, that
// the day cutoff hour round-trips and is put back, that a fuel expense goes pending → approved
// → paid on the PV- series, that the logo round-trips and a data:text/html one is refused, and
// that a branch's own fiscal identifiers round-trip through /api/catalog/branches.
// 2026-10-05 additions (the owner): every branch carries a read-only three-digit code (100–999); a
// customer's code is drawn by the server — Cust-… for a person, Comp-… for a company, with a
// location's code inside — and one sent is ignored on create and on edit; a new hire's number is
// drawn the same way (Emp-…); a tax number is a company's, and a person sent one is refused with
// taxIdCompanyOnly.
// 2026-10-05, PinSay #301/#302: an employee's name in two parts composes the full name first name
// first; a named contract wording is kept and counted on its own, named by a draft contract, refused
// removal while the draft prints it, and removed once it does not — its text kept in its history.
const BASE = process.env.BASE || 'http://127.0.0.1:5123';
// The credentials are configuration, not a constant: a database seeded through
// Bootstrap__AdminUsername/AdminPassword uses the supplied account, which is the whole
// point of docs/07 §6.0 item 1 — so the restore drill and the MySQL CI job pass their own pair.
// Supply the test account password through ADMIN_PASSWORD; there is no default.
const ADMIN_USER = process.env.ADMIN_USER || 'admin';
const ADMIN_PASSWORD = requiredPassword('ADMIN_PASSWORD');
// Labels only; the script neither knows nor cares which provider is behind the API.
const PROVIDER = process.env.PROVIDER || 'the configured provider';

let failures = 0;
const check = (name, ok, extra = '') => {
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${extra ? ' — ' + extra : ''}`);
  if (!ok) failures++;
};

async function json(path, options = {}, token) {
  const res = await fetch(BASE + path, {
    ...options,
    headers: {
      'content-type': 'application/json',
      ...(token ? { authorization: 'Bearer ' + token } : {}),
      ...(options.headers || {}),
    },
  });
  const text = await res.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { body = text; }
  return { status: res.status, body };
}

/** A file as bytes — a logo behind its signed-in link. */
async function bytes(path, token) {
  const res = await fetch(BASE + path, { headers: token ? { authorization: 'Bearer ' + token } : {} });
  const type = (res.headers.get('content-type') || '').split(';')[0].trim();
  return { status: res.status, type, buffer: Buffer.from(await res.arrayBuffer()) };
}

/** A stored logo as the data URL that would put it back, or null when there is none. */
async function logoAsDataUrl(link, token) {
  if (!link) return null;
  const file = await bytes(link, token);
  return file.status === 200 ? 'data:' + file.type + ';base64,' + file.buffer.toString('base64') : null;
}

(async () => {
  const login = await json('/api/auth/login', {
    method: 'POST',
    body: JSON.stringify({ username: ADMIN_USER, password: ADMIN_PASSWORD, deviceCode: 'C01' }),
  });
  check(`admin signs in against ${PROVIDER}`, login.status === 200, 'HTTP ' + login.status);
  const token = login.body?.token;
  if (!token) { console.log('\nno token, stopping'); process.exit(1); }

  const methods = await json('/api/treasury/payment-methods', {}, token);
  const builtIns = (methods.body || []).filter((m) => m.isBuiltIn);
  check('the five built-in tenders are readable', builtIns.length === 5, 'count ' + builtIns.length);
  const cash = (methods.body || []).find((m) => m.key === 'cash');
  check('cash is confirmed on the spot and reaches the drawer',
    !!cash && cash.requiresConfirmation === false && cash.affectsCashDrawer === true);

  // An operator-defined tender: the thing that was impossible before phase 1.
  const created = await json('/api/treasury/payment-methods', {
    method: 'POST',
    body: JSON.stringify({
      key: 'ccp-' + Date.now().toString(36), nameAr: 'حوالة بريدية', nameFr: 'Virement CCP', nameEn: 'Postal transfer',
      requiresConfirmation: true, requiresReference: true, requiresInstrumentDetails: false,
      isActive: true, sortOrder: 10,
    }),
  }, token);
  check('an operator-defined tender can be added', created.status === 201, 'HTTP ' + created.status);
  check('an added tender is generic and needs confirming',
    created.body?.behavior === 'Generic' && created.body?.requiresConfirmation === true);
  check('an added tender never reaches the cash drawer', created.body?.affectsCashDrawer === false);

  const cashBoxes = await json('/api/treasury/cash-boxes', {}, token);
  check('cash boxes report a balance', Array.isArray(cashBoxes.body) && cashBoxes.body.length >= 1,
    'count ' + (cashBoxes.body?.length ?? 'n/a'));

  // Money summed client-side because SQLite stores decimal as TEXT; confirm it is right here too.
  const remitCatalogue = await json('/api/catalog/products', {}, token);
  const remitProduct = (Array.isArray(remitCatalogue.body) ? remitCatalogue.body : remitCatalogue.body?.items || [])
    .find((p) => !p.isDeleted && Number(p.price) > 0);
  const main = (cashBoxes.body || []).find((b) => b.isMain);
  const branch = (cashBoxes.body || []).find((b) => !b.isMain);
  if (!remitProduct) {
    console.log('SKIP  the remittance checks need a priced product; this database has none');
  } else if (main && branch) {
    const badRemit = await json('/api/treasury/remittances', {
      method: 'POST',
      body: JSON.stringify({
        fromCashBoxId: branch.id,
        toCashBoxId: main.id,
        amount: Number(branch.balance) + 1000,
        reference: 'SMOKE-OVERDRAFT',
        note: null,
      }),
    }, token);
    const afterBad = await json('/api/treasury/cash-boxes', {}, token);
    const branchAfterBad = (afterBad.body || []).find((b) => b.id === branch.id);
    const mainAfterBad = (afterBad.body || []).find((b) => b.id === main.id);
    check('a remittance of more than the source box holds is refused',
      badRemit.status === 400
        && branchAfterBad?.balance === branch.balance
        && mainAfterBad?.balance === main.balance,
      'HTTP ' + badRemit.status);

    const price = Number(remitProduct.price);
    const qty = Math.max(1, Math.ceil(1500 / price));
    const total = qty * price;
    const receipt = 'SMOKE-REMIT-' + Date.now().toString(36).toUpperCase();
    let localSeq = Date.now();
    const sale = () => ({
      id: crypto.randomUUID(), reference: receipt, localSeq: localSeq++, soldAtUtc: new Date().toISOString(), customerId: null,
      cashierUserId: login.body.user.id, cashierName: login.body.user.fullName,
      subTotalHt: total, taxTotal: 0, discountTotal: 0, grandTotalTtc: total, paidAmount: total, changeAmount: 0,
      paymentMethod: 'Cash', status: 'Completed', refundOfSaleId: null, note: 'provider smoke', taxMode: 'untaxed',
      lines: [{ id: crypto.randomUUID(), productId: remitProduct.id, sku: remitProduct.sku, nameSnapshot: remitProduct.nameFr || remitProduct.sku,
        qty, unitPrice: price, taxRate: 0, lineDiscount: 0, lineTotalTtc: total, lineTaxAmount: 0 }],
      payments: [{ id: crypto.randomUUID(), method: 'cash', amount: total, reference: null, methodKey: 'cash' }],
    });
    const push = (s) => json('/api/sync/push', { method: 'POST', body: JSON.stringify({ deviceCode: 'C01', sales: [s] }) }, token);
    const pushed = await push(sale());
    check('the cash sale that funds the remittance is recorded', pushed.status === 200 && pushed.body?.accepted === 1,
      'HTTP ' + pushed.status + ' ' + JSON.stringify(pushed.body?.results?.[0] ?? pushed.body).slice(0, 160));

    const afterSale = await json('/api/treasury/cash-boxes', {}, token);
    const boxesBeforePush = afterBad.body || [];
    const boxesAfterSale = afterSale.body || [];
    const source = boxesAfterSale.find((b) => {
      const prev = boxesBeforePush.find((p) => p.id === b.id);
      return prev && Math.abs((Number(b.balance) - Number(prev.balance)) - total) < 0.01;
    }) || boxesAfterSale.find((b) => {
      const prev = boxesBeforePush.find((p) => p.id === b.id);
      return prev && Number(b.balance) > Number(prev.balance);
    }) || branch;
    const destination = source.isMain
      ? (boxesAfterSale.find((b) => b.branchId !== source.branchId) || boxesAfterSale.find((b) => b.id !== source.id))
      : (boxesAfterSale.find((b) => b.isMain) || boxesAfterSale.find((b) => b.id !== source.id));

    const remit = await json('/api/treasury/remittances', {
      method: 'POST',
      body: JSON.stringify({ fromCashBoxId: source.id, toCashBoxId: destination.id, amount: 1500, reference: 'SMOKE-1', note: null }),
    }, token);
    check('a remittance is recorded', remit.status === 200, 'HTTP ' + remit.status);

    const after = await json('/api/treasury/cash-boxes', {}, token);
    const destAfter = (after.body || []).find((b) => b.id === destination.id);
    const sourceAfter = (after.body || []).find((b) => b.id === source.id);
    check('the destination gained exactly what the source lost',
      destAfter && sourceAfter
        && Math.abs((destAfter.balance - destination.balance) - 1500) < 0.005
        && Math.abs((source.balance - sourceAfter.balance) - 1500) < 0.005,
      `destination ${destination.balance}->${destAfter?.balance}, source ${source.balance}->${sourceAfter?.balance}`);
  }

  const products = await json('/api/catalog/products', {}, token);
  const tracked = (products.body || []).find((p) => p.trackStock);
  check('the catalog reads back', !!tracked, tracked ? tracked.sku : 'none');

  if (tracked) {
    const receipt = await json('/api/catalog/stock/receiving', {
      method: 'POST',
      body: JSON.stringify({
        lines: [{ productId: tracked.id, qty: 40, unitCost: 900, assetUnitValue: 600 }],
        reference: 'SMOKE-RCV', note: null,
      }),
    }, token);
    check('goods can be received into the warehouse', receipt.status === 200, 'HTTP ' + receipt.status);

    const branchId = receipt.body?.branchId;
    const move = await json('/api/catalog/stock/state', {
      method: 'POST',
      body: JSON.stringify({ productId: tracked.id, branchId, qty: 5, from: 'Available', to: 'Maintenance', reason: 'smoke' }),
    }, token);
    check('stock moves between states', move.status === 200, 'HTTP ' + move.status);
    check('owned is unchanged by a state move, only availability falls',
      move.body && move.body.maintenance >= 5,
      JSON.stringify(move.body));

    const writeOff = await json('/api/catalog/stock/state', {
      method: 'POST',
      body: JSON.stringify({ productId: tracked.id, branchId, qty: 2, from: 'Maintenance', to: 'WrittenOff', reason: 'smoke' }),
    }, token);
    check('a write-off is accepted', writeOff.status === 200, 'HTTP ' + writeOff.status);

    const back = await json('/api/catalog/stock/state', {
      method: 'POST',
      body: JSON.stringify({ productId: tracked.id, branchId, qty: 1, from: 'WrittenOff', to: 'Available', reason: 'smoke' }),
    }, token);
    check('written-off stock cannot be brought back', back.status === 400, 'HTTP ' + back.status);

    const availability = await json('/api/catalog/stock/availability/' + tracked.id, {}, token);
    check('an admin may see every branch position', availability.status === 200, 'HTTP ' + availability.status);
  }

  // ---------- phase 3: the ledger ----------
  // The reports run decimal aggregates client-side because SQLite stores decimal as TEXT;
  // this is where that assumption is checked against a provider that does not.

  const accounts = await json('/api/ledger/accounts', {}, token);
  const byCode = Object.fromEntries((accounts.body || []).map((a) => [a.code, a]));
  check('the default chart of accounts is seeded', (accounts.body || []).length >= 20,
    'count ' + (accounts.body?.length ?? 'n/a'));
  check('cash is an asset and tax collected is a liability',
    byCode['530']?.type === 'Asset' && byCode['4457']?.type === 'Liability');
  check('there is an opening-balance equity account to post against',
    byCode['890']?.type === 'Equity');

  // A system account may be renamed but never re-typed: code posts to it by code.
  const retype = await json('/api/ledger/accounts/' + byCode['530'].id, {
    method: 'PUT',
    body: JSON.stringify({ ...byCode['530'], type: 'Expense' }),
  }, token);
  check('a system account cannot be moved to another side of the balance sheet',
    retype.status === 400, 'HTTP ' + retype.status);

  const period = {
    from: new Date(Date.now() - 86400e3).toISOString(),
    to: new Date(Date.now() + 86400e3).toISOString(),
  };
  const q = `from=${encodeURIComponent(period.from)}&to=${encodeURIComponent(period.to)}`;

  const receivingEntries = await json(`/api/ledger/entries?sourceType=receiving&${q}`, {}, token);
  const receivingEntry = (receivingEntries.body || [])[0];
  check('the receiving note posted an entry', !!receivingEntry, 'HTTP ' + receivingEntries.status);
  if (receivingEntry) {
    const debits = receivingEntry.lines.reduce((t, l) => t + l.debit, 0);
    const credits = receivingEntry.lines.reduce((t, l) => t + l.credit, 0);
    check('the receiving entry balances', Math.abs(debits - credits) < 0.001, `${debits} vs ${credits}`);
    check('receiving never touches profit or loss',
      !receivingEntry.lines.some((l) => l.accountType === 'Income' || l.accountType === 'Expense'),
      receivingEntry.lines.map((l) => l.accountCode + ':' + l.accountType).join(' '));
    check('the entry leads back to its document',
      receivingEntry.sourceType === 'receiving' && !!receivingEntry.sourceId);
  }

  // ⚠ The trap. A remittance is cash moving between two asset locations, never income at the
  // safe and never an expense at the branch.
  const remitEntries = await json(`/api/ledger/entries?sourceType=remittance&${q}`, {}, token);
  const remitEntry = (remitEntries.body || [])[0];
  check('the remittance posted an entry', !!remitEntry, 'HTTP ' + remitEntries.status);
  if (remitEntry) {
    check('a remittance never reaches the P&L',
      remitEntry.lines.every((l) => l.accountCode === '530'),
      remitEntry.lines.map((l) => l.accountCode).join(' '));
    check('a remittance is marked so consolidation can drop it', remitEntry.isInterBranch === true);
    check('the two sides sit at different branches',
      new Set(remitEntry.lines.map((l) => l.branchId)).size === 2);
  }

  const trial = await json(`/api/ledger/reports/trial-balance?${q}`, {}, token);
  check('the trial balance balances', trial.body?.balanced === true,
    `${trial.body?.totalDebit} vs ${trial.body?.totalCredit}`);

  const pl = await json(`/api/ledger/reports/profit-and-loss?${q}`, {}, token);
  check('the consolidated P&L eliminates inter-branch movements',
    pl.body?.consolidated === true && pl.body?.eliminatedInterBranch === 0,
    'eliminated ' + pl.body?.eliminatedInterBranch);

  const sheet = await json('/api/ledger/reports/balance-sheet', {}, token);
  check('the balance sheet balances', sheet.body?.balanced === true,
    `assets ${sheet.body?.totalAssets} vs ${sheet.body?.totalLiabilities} + ${sheet.body?.totalEquity} + ${sheet.body?.netResult}`);

  if (byCode['310']) {
    const statement = await json(`/api/ledger/reports/account-statement?accountId=${byCode['310'].id}&${q}`, {}, token);
    check('an account statement reads back with a running balance',
      statement.status === 200 && Array.isArray(statement.body?.rows), 'HTTP ' + statement.status);
  }

  // Nothing is deleted; a correction is a reversing entry.
  if (receivingEntry) {
    const reversal = await json('/api/ledger/entries/' + receivingEntry.id + '/reverse', {
      method: 'POST',
      body: JSON.stringify({ reason: 'SMOKE — delivery never arrived' }),
    }, token);
    check('an entry can be reversed', reversal.status === 200, 'HTTP ' + reversal.status);

    const twice = await json('/api/ledger/entries/' + receivingEntry.id + '/reverse', {
      method: 'POST',
      body: JSON.stringify({ reason: 'SMOKE — again' }),
    }, token);
    check('the same entry cannot be reversed twice', twice.status === 400, 'HTTP ' + twice.status);

    const after = await json(`/api/ledger/reports/trial-balance?${q}`, {}, token);
    check('the books still balance after a reversal', after.body?.balanced === true);
  }

  // ---------- phase 4: renting ----------
  // A contract is a chain of documents, and every link touches something a provider can get
  // wrong: decimals the engine multiplies, a lock enforced inside the filter's transaction,
  // stock buckets at two branches, and four income accounts that must not be merged.

  const day = (offset) => new Date(Date.now() + offset * 86400e3).toISOString();

  const settings = await json('/api/rentals/settings', {}, token);
  check('the rental settings read back', settings.status === 200, 'HTTP ' + settings.status);
  // The migration's generated column default was 0, which would price every overdue day at
  // nothing. This is the check that the corrected default actually reached the row here.
  check('the overdue multiplier is a usable figure and not zero',
    settings.body?.overdueMultiplier >= 1, 'multiplier ' + settings.body?.overdueMultiplier);
  check('the deposit is switched off', settings.body?.depositEnabled === false);

  const branches = await json('/api/catalog/branches', {}, token);
  const depot = (branches.body || []).find((b) => b.isMain);
  const other = (branches.body || []).find((b) => !b.isMain && b.isActive);
  // Every location carries a three-digit code, 100 to 999, drawn by the server and read-only (owner,
  // 2026-10-05; three digits since 2026-10-19); it is the location part of every customer and
  // employee number drawn since. Which location a number takes is the server's business, so a drawn
  // number is only checked to name one.
  const BRANCH_CODE = /^[1-9]\d{2}$/;
  const branchCodes = new Set((branches.body || []).map((b) => b.code));
  /** The three digits after a drawn number's prefix: Cust-3612610001 → "361". */
  const locationOf = (number) => String(number ?? '').replace(/^[A-Za-z]+-/, '').slice(0, 3);
  check('every branch carries its own three-digit code',
    (branches.body || []).length > 0 && (branches.body || []).every((b) => BRANCH_CODE.test(b.code ?? ''))
    && branchCodes.size === branches.body.length,
    (branches.body || []).map((b) => b.code).join(' '));
  const customers = await json('/api/catalog/customers', {}, token);
  const onAccount = (customers.body || []).find((c) => c.creditLimit > 0) || (customers.body || [])[0];

  // ---------- the customer's kind and the company's authorised representative ----------
  // On a database migrated in place every pre-release customer is Unspecified — the migration
  // never guesses and the seeder does not re-run — so every contract below names its signatory
  // explicitly rather than relying on a prefill that only a person or a represented company gets.
  check('customers carry their kind as a word and a representative field',
    Array.isArray(customers.body) && customers.body.length > 0
    && customers.body.every((c) => ['Person', 'Company', 'Unspecified'].includes(c.kind) && 'representative' in c),
    [...new Set((customers.body || []).map((c) => c.kind))].join(' '));

  const stamp = Date.now().toString(36).toUpperCase();
  // No code is typed (owner, 2026-10-05): the server draws one on create — the prefix (Cust- for a
  // person, Comp- for a company, each its own counter), the location's three digits, YYMM, then a
  // sequence: Cust-3612610001 — and ignores one sent, on create and on update. The month and the
  // number are not asserted: CI runs on any date, on a database that may already hold customers.
  // Everything after the create reads the code from its answer.
  const PERSON_CODE = /^Cust-[1-9]\d{2}\d{4}\d{3,}$/;
  const COMPANY_CODE = /^Comp-[1-9]\d{2}\d{4}\d{3,}$/;
  const blankCustomer = {
    phone: null, address: null, taxId: null, email: null, birthDate: null, creditLimit: 0, idDocumentNumber: null,
  };
  const unrepresented = await json('/api/catalog/customers', {
    method: 'POST',
    body: JSON.stringify({ ...blankCustomer, fullName: 'SARL Sans Gérant', kind: 'Company', representative: null }),
  }, token);
  check('a company is refused without an authorised representative', unrepresented.status === 400, 'HTTP ' + unrepresented.status);

  // The tax number is a company's (owner, 2026-10-05), so this is the customer that carries one.
  const companyTaxId = '0016' + stamp;
  const company = await json('/api/catalog/customers', {
    method: 'POST',
    body: JSON.stringify({
      ...blankCustomer, taxId: companyTaxId, fullName: 'SARL Smoke ' + stamp, kind: 'Company',
      representative: { fullName: 'Smoke Gérant', idDocumentNumber: '111 111 111', phone: null, note: 'gérant' },
    }),
  }, token);
  check('a company is registered with its representative in one save', company.status === 201, 'HTTP ' + company.status);
  check('the new company answers with its current representative',
    company.body?.kind === 'Company' && company.body?.representative?.fullName === 'Smoke Gérant',
    JSON.stringify({ kind: company.body?.kind, representative: company.body?.representative?.fullName }));
  check('a company is given a code the server drew from the company series, at one of the locations',
    COMPANY_CODE.test(company.body?.code ?? '') && branchCodes.has(locationOf(company.body?.code)) && !!company.body?.id,
    String(company.body?.code));
  check('a company keeps its tax number', company.body?.taxId === companyTaxId, String(company.body?.taxId));

  // A natural person has no tax number: one sent is refused with a code, never saved or silently dropped.
  const taxedPerson = await json('/api/catalog/customers', {
    method: 'POST',
    body: JSON.stringify({ ...blankCustomer, taxId: '000116001234567', fullName: 'Smoke Personne NIF ' + stamp, kind: 'Person' }),
  }, token);
  check('a person is refused a tax number, as a code',
    taxedPerson.status === 400 && taxedPerson.body?.code === 'taxIdCompanyOnly',
    'HTTP ' + taxedPerson.status + ' ' + JSON.stringify(taxedPerson.body));

  if (company.status === 201) {
    const first = await json('/api/catalog/customers/' + company.body.id + '/representatives', {}, token);
    check('the first appointment is one open history row',
      first.status === 200 && first.body?.length === 1 && first.body[0].toUtc === null,
      'HTTP ' + first.status + ' rows ' + first.body?.length);

    // The representative leaves: one write ends the old row and adds the new one.
    const changed = await json('/api/catalog/customers/' + company.body.id + '/representatives', {
      method: 'POST',
      body: JSON.stringify({ fullName: 'Smoke Successeur', idDocumentNumber: null, phone: null, note: 'nouveau gérant', fromUtc: null }),
    }, token);
    const open = (changed.body || []).filter((r) => r.toUtc === null);
    check('changing the representative ends the old row and adds the new one',
      changed.status === 200 && changed.body?.length === 2 && open.length === 1 && open[0].fullName === 'Smoke Successeur',
      'HTTP ' + changed.status + ' rows ' + changed.body?.length + ' open ' + open.length);
    check('the old representative is closed exactly when the new one starts, never erased',
      (changed.body || []).some((r) => r.fullName === 'Smoke Gérant' && r.toUtc === open[0]?.fromUtc));
  }

  // ---------- the owner's paperwork (2026-09-30): who signs, where the goods go, who drives ----------
  // A person signs for themselves, so the renter below is a person whose five identity fields
  // (document type, number, issuer, issue date, birth date) are on the customer record — the
  // first issue note refuses a contract while any of them is missing. Every contract names one
  // of the customer's sites, and every issue and return note names the driver and the truck.
  const renterFields = {
    ...blankCustomer, fullName: 'Smoke Locataire ' + stamp, kind: 'Person',
    creditLimit: 5000000, phone: '0555 00 00 00', birthDate: '1985-04-12T00:00:00Z',
    idDocumentNumber: '109 876 543', idDocumentType: 'nationalCard',
    idDocumentIssuer: 'Daïra de Bab El Oued', idDocumentIssuedOn: '2019-06-01',
  };
  // A code is sent here on purpose, as an older client would: the server ignores it and draws one.
  const typedCode = 'SMKP' + stamp;
  const renter = await json('/api/catalog/customers', {
    method: 'POST',
    body: JSON.stringify({ ...renterFields, code: typedCode }),
  }, token);
  check('a person is registered with the identity the paper contract records',
    renter.status === 201 && renter.body?.idDocumentType === 'nationalCard'
    && renter.body?.idDocumentIssuer === 'Daïra de Bab El Oued' && renter.body?.idDocumentIssuedOn === '2019-06-01',
    'HTTP ' + renter.status + ' ' + JSON.stringify({ type: renter.body?.idDocumentType, on: renter.body?.idDocumentIssuedOn }));
  check('a person is given a code the server drew from the person series, at one of the locations, not the one typed',
    PERSON_CODE.test(renter.body?.code ?? '') && branchCodes.has(locationOf(renter.body?.code))
    && renter.body?.code !== typedCode && !!renter.body?.id,
    String(renter.body?.code));
  if (renter.status === 201) {
    // The stored code never changes: an edit that sends another one keeps the drawn code.
    const edited = await json('/api/catalog/customers/' + renter.body.id, {
      method: 'PUT',
      body: JSON.stringify({ ...renterFields, code: typedCode + 'X' }),
    }, token);
    check('editing a customer never changes its code',
      edited.status === 200 && edited.body?.code === renter.body.code,
      'HTTP ' + edited.status + ' ' + edited.body?.code + ' (drawn ' + renter.body.code + ')');
  }

  const newSite = (customerId, name) => json('/api/customers/' + customerId + '/sites', {
    method: 'POST', body: JSON.stringify({ name, address: 'Lot 12, zone industrielle', phone: null }),
  }, token);
  const renterSite = renter.status === 201 ? await newSite(renter.body.id, 'Chantier Smoke ' + stamp) : { status: 0 };
  check('a site can be added to a customer', renterSite.status === 201 && !!renterSite.body?.id, 'HTTP ' + renterSite.status);

  const transport = { driverName: 'Smoke Chauffeur', driverPhone: '0661 11 22 33', truckNumber: '01234-116-16' };

  if (tracked && depot && other && renter.status === 201 && renterSite.status === 201) {
    // The rent is a daily rate (2026-10-06): 100 a day; the overdue price is the multiplier
    // applied once, at signing.
    const expectedOverdue = Math.round(100 * settings.body.overdueMultiplier * 100) / 100;
    const party = { customerId: renter.body.id, branchId: depot.id, siteId: renterSite.body.id };

    // Answer 9: the site is mandatory, and refused in words rather than left blank on the paper.
    const siteless = await json('/api/rentals/contracts', {
      method: 'POST',
      body: JSON.stringify({
        ...party, siteId: null, notes: 'SMOKE - no site',
        lines: [{ productId: tracked.id, qty: 1, durationDays: 30, rentUnitPrice: 100, damageUnitPrice: 2000 }],
      }),
    }, token);
    check('a contract without a site is refused', siteless.status === 400 && /site/i.test(JSON.stringify(siteless.body)),
      'HTTP ' + siteless.status + ' ' + JSON.stringify(siteless.body));

    const contract = await json('/api/rentals/contracts', {
      method: 'POST',
      body: JSON.stringify({
        ...party, notes: 'SMOKE',
        lines: [{ productId: tracked.id, qty: 10, durationDays: 30, rentUnitPrice: 100, damageUnitPrice: 2000 }],
      }),
    }, token);
    check('a rental contract can be raised', contract.status === 201, 'HTTP ' + contract.status);
    check('the contract freezes the site and signs in the person\'s own name and identity',
      contract.body?.siteName === 'Chantier Smoke ' + stamp && contract.body?.signatoryName === renter.body.fullName
      && contract.body?.signatoryIdIssuer === 'Daïra de Bab El Oued' && !!contract.body?.signatoryBirthDate,
      JSON.stringify({ site: contract.body?.siteName, name: contract.body?.signatoryName, born: contract.body?.signatoryBirthDate }));

    const line = contract.body?.lines?.[0];
    check('the overdue price is derived from the multiplier and signed on the line',
      line && Math.abs(line.overdueUnitPrice - expectedOverdue) < 0.01,
      `${line?.overdueUnitPrice} vs ${expectedOverdue}`);
    check('a fresh contract is unlocked and reports what is left to issue',
      contract.body?.isLocked === false && line?.leftToIssueQty === 10);
    check('the deposit is invisible while it is switched off',
      contract.body?.depositAmount === null || contract.body?.depositAmount === undefined);

    // Drift decision 1 (docs/07 §3.4, accepted): the company multiplier is a FLOOR for a
    // negotiated overdue price — a manager may sign above it, never below — and the refusal
    // names the floor so the clerk is told the least acceptable figure, not that "a rule" failed.
    const underFloor = await json('/api/rentals/contracts', {
      method: 'POST',
      body: JSON.stringify({
        ...party, notes: 'SMOKE - under the floor',
        lines: [{ productId: tracked.id, qty: 1, durationDays: 30, rentUnitPrice: 100, damageUnitPrice: 2000,
          overdueUnitPrice: Math.round((expectedOverdue - 1) * 100) / 100 }],
      }),
    }, token);
    check('a signed overdue price below the company floor is refused', underFloor.status === 400, 'HTTP ' + underFloor.status);
    const floorNamed = /below the company floor of ([\d.]+)/.exec(JSON.stringify(underFloor.body));
    check('the refusal names the floor the price fell under',
      !!floorNamed && Math.abs(parseFloat(floorNamed[1]) - expectedOverdue) < 0.01,
      floorNamed ? 'floor ' + floorNamed[1] : JSON.stringify(underFloor.body));

    const edited = await json('/api/rentals/contracts/' + contract.body.id, {
      method: 'PUT',
      body: JSON.stringify({
        ...party, notes: 'SMOKE - revised',
        lines: [{ productId: tracked.id, qty: 12, durationDays: 30, rentUnitPrice: 100, damageUnitPrice: 2000 }],
      }),
    }, token);
    check('an unissued contract can still be edited', edited.status === 200, 'HTTP ' + edited.status);

    const lineId = edited.body?.lines?.[0]?.id;

    // Answer 16: the driver and the truck are mandatory on both notes, and the note is refused
    // rather than warned about. A refused note is not a note: it locks and issues nothing.
    const driverless = await json('/api/rentals/contracts/' + contract.body.id + '/issue-notes', {
      method: 'POST',
      body: JSON.stringify({
        branchId: depot.id, issuedAtUtc: day(-40), note: 'SMOKE - no driver', truckNumber: transport.truckNumber,
        lines: [{ contractLineId: lineId, qty: 6 }],
      }),
    }, token);
    check('an issue note without a driver is refused', driverless.status === 400
      && /driver/i.test(JSON.stringify(driverless.body)), 'HTTP ' + driverless.status + ' ' + JSON.stringify(driverless.body));
    const afterDriverless = await json('/api/rentals/contracts/' + contract.body.id, {}, token);
    check('the refused note locked and issued nothing',
      afterDriverless.body?.isLocked === false && afterDriverless.body?.lines?.[0]?.leftToIssueQty === 12,
      JSON.stringify({ locked: afterDriverless.body?.isLocked, left: afterDriverless.body?.lines?.[0]?.leftToIssueQty }));

    const issue = await json('/api/rentals/contracts/' + contract.body.id + '/issue-notes', {
      method: 'POST',
      body: JSON.stringify({
        branchId: depot.id, issuedAtUtc: day(-40), note: 'SMOKE', ...transport,
        lines: [{ contractLineId: lineId, qty: 6 }],
      }),
    }, token);
    check('goods can be issued against a contract', issue.status === 201, 'HTTP ' + issue.status);
    check('the issue note carries the driver and the truck it went out on',
      issue.body?.driverName === transport.driverName && issue.body?.truckNumber === transport.truckNumber,
      JSON.stringify({ driver: issue.body?.driverName, truck: issue.body?.truckNumber }));

    // The rule the whole module hangs off: 6 units x 30 days x 100 a day.
    check('the full agreed period is invoiced when the goods go out',
      issue.body && Math.abs(issue.body.periodRent - 18000) < 0.01, 'periodRent ' + issue.body?.periodRent);

    const locked = await json('/api/rentals/contracts/' + contract.body.id, {
      method: 'PUT',
      body: JSON.stringify({
        ...party,
        lines: [{ productId: tracked.id, qty: 99, durationDays: 30, rentUnitPrice: 1, damageUnitPrice: 1 }],
      }),
    }, token);
    check('a contract locks as soon as it has been issued against', locked.status === 409, 'HTTP ' + locked.status);

    // Owner, 2026-10-06: a contract is called off only before its exit note, with a reason, and
    // then only the Admin sees it.
    const lateCancel = await json('/api/rentals/contracts/' + contract.body.id + '/cancel', {
      method: 'POST', body: JSON.stringify({ reason: 'SMOKE - too late' }),
    }, token);
    check('a contract with an exit note is not cancelled', lateCancel.status === 409, 'HTTP ' + lateCancel.status);
    const draft = await json('/api/rentals/contracts', {
      method: 'POST',
      body: JSON.stringify({
        ...party, notes: 'SMOKE - to cancel',
        lines: [{ productId: tracked.id, qty: 1, durationDays: 30, rentUnitPrice: 100, damageUnitPrice: 2000 }],
      }),
    }, token);
    const cancelled = await json('/api/rentals/contracts/' + draft.body?.id + '/cancel', {
      method: 'POST', body: JSON.stringify({ reason: 'SMOKE - changed his mind' }),
    }, token);
    check('a contract with no exit note is cancelled with its reason',
      cancelled.status === 200 && cancelled.body?.status === 'Cancelled' && cancelled.body?.cancelReason === 'SMOKE - changed his mind',
      'HTTP ' + cancelled.status + ' ' + cancelled.body?.status);
    const cancelledList = await json('/api/rentals/contracts?status=Cancelled', {}, token);
    check('the Admin finds it among the cancelled contracts',
      (cancelledList.body || []).some((c) => c.id === draft.body?.id), 'HTTP ' + cancelledList.status);

    const outAt = await json('/api/catalog/stock/availability/' + tracked.id, {}, token);
    const depotStock = (outAt.body || []).find((b) => b.branchId === depot.id);
    check('issuing moved available to on rent without changing what is owned',
      depotStock && depotStock.onRent >= 6, JSON.stringify(depotStock));

    // The owner, 2026-09-30: the return comes back to the contract's branch, and a transfer
    // moves it on afterwards. Another branch is refused in words, before anything moves.
    const ownedBefore = (outAt.body || []).reduce((t, b) => t + b.owned, 0);
    const returnLines = [{ contractLineId: lineId, qtyOk: 5, qtyMissing: 1 }];
    if (other) {
      const elsewhere = await json('/api/rentals/contracts/' + contract.body.id + '/return-notes', {
        method: 'POST',
        body: JSON.stringify({ branchId: other.id, returnedAtUtc: day(0), note: 'SMOKE', ...transport, lines: returnLines }),
      }, token);
      check("a return at a branch other than the contract's is refused in words",
        elsewhere.status === 400 && /contract's branch/.test(JSON.stringify(elsewhere.body)),
        'HTTP ' + elsewhere.status + ' ' + JSON.stringify(elsewhere.body));
    }
    // Charged less an amount (the owner, 2026-10-22): the deduction and the late total it came off
    // are decimals this provider stores on the note.
    const lateDeduction = 100;
    const returned = await json('/api/rentals/contracts/' + contract.body.id + '/return-notes', {
      method: 'POST',
      body: JSON.stringify({
        branchId: depot.id, returnedAtUtc: day(0), note: 'SMOKE', ...transport, lines: returnLines,
        chargeOverdue: true, lateAmountWaived: lateDeduction, lateWaiverReason: 'SMOKE - loyal customer',
      }),
    }, token);
    check('goods can be returned', returned.status === 201, 'HTTP ' + returned.status);
    check("a return at the contract's branch moves no stock between branches",
      (returned.body?.transfers || []).length === 0, JSON.stringify(returned.body?.transfers));
    // Issued equals returned plus settled, so the goods are back — but the contract closes only
    // once it is paid (owner, 2026-10-02, answers 15 and 22), which this unpaid one is not.
    check('goods all back on an unpaid contract leave it open',
      returned.body?.contractClosed === false);
    const contractAfterReturn = await json('/api/rentals/contracts/' + contract.body.id, {}, token);
    check('the contract records that its goods are back, and what it is still owed',
      !!contractAfterReturn.body?.goodsBackAtUtc && contractAfterReturn.body?.owed > contractAfterReturn.body?.paid,
      JSON.stringify({ goodsBack: contractAfterReturn.body?.goodsBackAtUtc, owed: contractAfterReturn.body?.owed, paid: contractAfterReturn.body?.paid }));

    // 6 units x 10 days over x the daily overdue price, less the deduction; one unit at the frozen 2 000.
    const kinds = Object.fromEntries((returned.body?.charges || []).map((c) => [c.kind, c]));
    check('a late return is charged overdue at the signed price, less the deduction',
      !!kinds.Overdue && Math.abs(kinds.Overdue.amount - (6 * 10 * expectedOverdue - lateDeduction)) < 1,
      'overdue ' + kinds.Overdue?.amount);
    const storedNotes = await json('/api/rentals/contracts/' + contract.body.id + '/return-notes', {}, token);
    const storedNote = (storedNotes.body || []).find((n) => n.id === returned.body?.id);
    check('the deduction and the late total it came off are stored on the note',
      storedNote?.lateAmountWaived === lateDeduction && Math.abs((storedNote?.lateAmountFull ?? 0) - 6 * 10 * expectedOverdue) < 1
        && storedNote?.lateDaysWaived == null && storedNote?.lateWaiverReason === 'SMOKE - loyal customer',
      JSON.stringify({ waived: storedNote?.lateAmountWaived, full: storedNote?.lateAmountFull, reason: storedNote?.lateWaiverReason }));
    check('a unit that never came back is charged at the price frozen on the contract',
      !!kinds.Missing && Math.abs(kinds.Missing.amount - 2000) < 0.01, 'missing ' + kinds.Missing?.amount);
    check('the agreed period is recorded on the return and marked as already invoiced',
      !!kinds.Rent && kinds.Rent.isPosted === false, JSON.stringify(kinds.Rent));

    const afterReturn = await json('/api/catalog/stock/availability/' + tracked.id, {}, token);
    const ownedAfter = (afterReturn.body || []).reduce((t, b) => t + b.owned, 0);
    check('only the unit that never came back left the business',
      Math.abs(ownedBefore - ownedAfter - 1) < 0.001, `${ownedBefore} -> ${ownedAfter}`);

    // Paid what is left on a receipt naming the contract, it closes (answers 15, 22).
    const remaining = Math.round(((contractAfterReturn.body?.owed || 0) - (contractAfterReturn.body?.paid || 0)) * 100) / 100;
    const payment = await json('/api/receivables/receipts', {
      method: 'POST',
      body: JSON.stringify({
        customerId: contract.body.customerId, branchId: depot.id, amount: remaining, methodKey: 'cash',
        kind: 'Payment', contractId: contract.body.id,
      }),
    }, token);
    check('a receipt naming the contract is taken and says it is for that contract',
      payment.status === 201 || payment.status === 200 ? payment.body?.contractId === contract.body.id : false,
      'HTTP ' + payment.status + ' ' + JSON.stringify(payment.body).slice(0, 200));

    const closed = await json('/api/rentals/contracts/' + contract.body.id, {}, token);
    check('a contract paid in full with its goods back reads back closed and locked',
      closed.body?.status === 'Closed' && closed.body?.isLocked === true, closed.body?.status);

    const settled = await json('/api/rentals/contracts/' + contract.body.id + '/settlement', {}, token);
    check('the settlement recomputes from the documents and agrees it is closed',
      settled.body?.isClosed === true && settled.body?.stillOutQty === 0,
      JSON.stringify({ closed: settled.body?.isClosed, out: settled.body?.stillOutQty }));

    // docs/07 §3.1: the list row carries its own answer — what went out, what came back, what
    // it cost — so the screen no longer settles every open contract to find out. The figures
    // are what was charged, and must agree with the recompute above on a closed contract.
    const rows = await json('/api/rentals/contracts', {}, token);
    const row = (rows.body || []).find((r) => r.id === contract.body.id);
    const rowKeys = ['issuedQty', 'settledQty', 'stillOutQty', 'chargeTotal', 'overdueTotal'];
    check('the contract list carries quantities and money on the row',
      !!row && rowKeys.every((k) => typeof row[k] === 'number'),
      row ? JSON.stringify(Object.fromEntries(rowKeys.map((k) => [k, row[k]]))) : 'HTTP ' + rows.status + ' row missing');
    check('the row says what went out and that nothing is still out',
      !!row && row.issuedQty === 6 && row.settledQty === 6 && row.stillOutQty === 0,
      JSON.stringify({ issued: row?.issuedQty, settled: row?.settledQty, out: row?.stillOutQty }));
    check('the money on the row is what the return note charged',
      !!row && Math.abs(row.overdueTotal - (kinds.Overdue?.amount ?? NaN)) < 0.01
      && Math.abs(row.chargeTotal - (settled.body?.chargeTotal ?? NaN)) < 0.01,
      `overdue ${row?.overdueTotal} vs ${kinds.Overdue?.amount}, charged ${row?.chargeTotal} vs ${settled.body?.chargeTotal}`);
    check('the deposit stays off the row while the switch is off',
      !!row && (row.depositAmount === null || row.depositAmount === undefined));

    // ---------- what it all posted ----------

    // REN-02 (owner, 2026-10-31): the rent is booked on the first day of the rental period — forty
    // days ago here — not on the day the note was typed, so the entry is looked for back there.
    const issueWindow = `from=${encodeURIComponent(day(-42))}&to=${encodeURIComponent(period.to)}`;
    const issueEntries = await json(`/api/ledger/entries?sourceType=rental-issue&${issueWindow}`, {}, token);
    const issueEntry = (issueEntries.body || [])[0];
    check('the issue note posted an entry', !!issueEntry, 'HTTP ' + issueEntries.status);
    if (issueEntry) {
      check("the issue entry is dated on the rental period's first day, not the day it was typed",
        new Date(issueEntry.entryDate) < new Date(period.from), issueEntry.entryDate);
      check('the issue entry balances',
        Math.abs(issueEntry.lines.reduce((t, l) => t + l.debit - l.credit, 0)) < 0.001);
      check('the hire is billed to the customer and recognised as rental income',
        issueEntry.lines.some((l) => l.accountCode === '411' && l.debit > 0)
        && issueEntry.lines.some((l) => l.accountCode === '706' && l.credit > 0),
        issueEntry.lines.map((l) => l.accountCode).join(' '));
    }

    const returnEntries = await json(`/api/ledger/entries?sourceType=rental-return&${q}`, {}, token);
    const returnEntry = (returnEntries.body || [])[0];
    check('the return note posted an entry', !!returnEntry, 'HTTP ' + returnEntries.status);
    if (returnEntry) {
      const codes = returnEntry.lines.map((l) => l.accountCode);
      check('overdue is its own income line, never merged into rent', codes.includes('708'));
      check('a unit that will not come back is compensation, not a sale', codes.includes('757'));
      // The one way this module could double revenue: the agreed period was invoiced when the
      // goods went out, so the return must not credit 706 again.
      check('the agreed period is never billed a second time on the way back',
        !returnEntry.lines.some((l) => l.accountCode === '706' && l.credit > 0),
        codes.join(' '));
    }

    // The return above came back to the contract's branch, so it made no transfer to post. The
    // implicit transfer of goods an Admin issued from another branch is pinned by RentalDocumentTests.

    // ---------- the extension, and the control on a retroactive one ----------

    const second = await json('/api/rentals/contracts', {
      method: 'POST',
      body: JSON.stringify({
        ...party, notes: 'SMOKE - extension',
        lines: [{ productId: tracked.id, qty: 4, durationDays: 30, rentUnitPrice: 100, damageUnitPrice: 2000 }],
      }),
    }, token);
    const secondLine = second.body?.lines?.[0]?.id;
    await json('/api/rentals/contracts/' + second.body.id + '/issue-notes', {
      method: 'POST',
      body: JSON.stringify({
        branchId: depot.id, issuedAtUtc: day(-40), ...transport, lines: [{ contractLineId: secondLine, qty: 4 }],
      }),
    }, token);

    // The period ran out ten days ago. Left open to everyone, every overdue becomes an
    // extension and the overdue line stops meaning anything.
    const unexplained = await json('/api/rentals/contracts/' + second.body.id + '/extensions', {
      method: 'POST',
      body: JSON.stringify({ extraDays: 10, contractLineId: secondLine }),
    }, token);
    check('a retroactive extension is refused without a written reason',
      unexplained.status === 400, 'HTTP ' + unexplained.status);

    const approved = await json('/api/rentals/contracts/' + second.body.id + '/extensions', {
      method: 'POST',
      body: JSON.stringify({ extraDays: 10, contractLineId: secondLine, reason: 'SMOKE - agreed by telephone' }),
    }, token);
    check('a manager may sign a retroactive extension with a reason',
      approved.status === 201, 'HTTP ' + approved.status);
    check('the extension records the day it was raised and the deadline it had to beat',
      approved.body?.isRetroactive === true && !!approved.body?.raisedOnUtc && !!approved.body?.periodEndsOnUtc,
      JSON.stringify({ raised: approved.body?.raisedOnUtc, due: approved.body?.periodEndsOnUtc }));

    // ---------- docs/07 §3.2: an item's own multiplier can be read, not only written ----------
    // Set after the contracts above so their expected overdue stays the company figure, and
    // cleared again at the end so a re-run starts from the same place.
    const overrideSet = await json('/api/rentals/settings/products/' + tracked.id, {
      method: 'PUT', body: JSON.stringify({ overdueMultiplier: 2 }),
    }, token);
    check('an item can be given its own overdue multiplier',
      overrideSet.status === 200 && overrideSet.body?.overdueMultiplier === 2 && overrideSet.body?.productId === tracked.id,
      'HTTP ' + overrideSet.status + ' ' + JSON.stringify(overrideSet.body));
    const overrides = await json('/api/rentals/settings/products', {}, token);
    const listed = (overrides.body || []).find((r) => r.productId === tracked.id);
    check('the multiplier just written is listed with the item it belongs to',
      overrides.status === 200 && !!listed && listed.overdueMultiplier === 2 && listed.sku === tracked.sku,
      'HTTP ' + overrides.status + ' ' + JSON.stringify(listed));
    const overrideOne = await json('/api/rentals/settings/products/' + tracked.id, {}, token);
    check('the same figure reads back by id', overrideOne.status === 200 && overrideOne.body?.overdueMultiplier === 2,
      'HTTP ' + overrideOne.status);
    const overrideCleared = await json('/api/rentals/settings/products/' + tracked.id, {
      method: 'PUT', body: JSON.stringify({ overdueMultiplier: null }),
    }, token);
    const overridesAfter = await json('/api/rentals/settings/products', {}, token);
    check('clearing the override takes the item off the list and back to the company figure',
      overrideCleared.status === 200 && overrideCleared.body?.overdueMultiplier === null
      && !(overridesAfter.body || []).some((r) => r.productId === tracked.id),
      'HTTP ' + overrideCleared.status + ' listed ' + (overridesAfter.body || []).length);

    // ---------- the signatory rule, at the lock ----------
    // Only a customer whose kind is not set yields a blank snapshot — a person is prefilled with
    // themselves, a company with its representative, and neither can be blanked through the API —
    // so this runs on a migrated row when the database has one, and says so when it has none.
    const unset = (customers.body || []).find((c) => c.kind === 'Unspecified');
    if (unset) {
      const unsetSite = await newSite(unset.id, 'Chantier Smoke inconnu ' + stamp);
      const unsigned = await json('/api/rentals/contracts', {
        method: 'POST',
        body: JSON.stringify({
          customerId: unset.id, branchId: depot.id, siteId: unsetSite.body?.id, notes: 'SMOKE - no signatory',
          lines: [{ productId: tracked.id, qty: 1, durationDays: 30, rentUnitPrice: 100, damageUnitPrice: 2000 }],
        }),
      }, token);
      check('a contract on a customer of unknown kind is a valid draft with no signatory',
        unsigned.status === 201 && unsigned.body?.signatoryRequired === true && !unsigned.body?.signatoryName,
        JSON.stringify({ status: unsigned.status, required: unsigned.body?.signatoryRequired, name: unsigned.body?.signatoryName }));

      const unsignedLine = unsigned.body?.lines?.[0]?.id;
      const note = { branchId: depot.id, issuedAtUtc: day(-1), note: 'SMOKE', ...transport, lines: [{ contractLineId: unsignedLine, qty: 1 }] };
      const refused = await json('/api/rentals/contracts/' + unsigned.body?.id + '/issue-notes', {
        method: 'POST', body: JSON.stringify(note),
      }, token);
      check('its first issue note is refused until the contract says who signed',
        refused.status === 400 && /no signatory/i.test(JSON.stringify(refused.body)),
        'HTTP ' + refused.status + ' ' + JSON.stringify(refused.body));

      const stillDraft = await json('/api/rentals/contracts/' + unsigned.body?.id, {}, token);
      check('a refused note locks nothing and issues nothing',
        stillDraft.body?.isLocked === false && stillDraft.body?.lines?.[0]?.leftToIssueQty === 1,
        JSON.stringify({ locked: stillDraft.body?.isLocked, left: stillDraft.body?.lines?.[0]?.leftToIssueQty }));

      const signed = await json('/api/rentals/contracts/' + unsigned.body?.id, {
        method: 'PUT',
        body: JSON.stringify({
          customerId: unset.id, branchId: depot.id, siteId: unsetSite.body?.id, notes: 'SMOKE - no signatory',
          // Nothing on an unknown-kind customer to take the identity from, so all five are typed.
          signatoryName: 'SMOKE Délégué', signatoryIdNumber: '222 333 444', signatoryIdType: 'passport',
          signatoryIdIssuer: "Wilaya d'Alger", signatoryIdIssuedOn: '2020-01-15', signatoryBirthDate: '1979-11-02',
          lines: [{ productId: tracked.id, qty: 1, durationDays: 30, rentUnitPrice: 100, damageUnitPrice: 2000 }],
        }),
      }, token);
      check('the signatory can be entered while the contract is still a draft',
        signed.status === 200 && signed.body?.signatoryName === 'SMOKE Délégué', 'HTTP ' + signed.status);

      const issuedNow = await json('/api/rentals/contracts/' + unsigned.body?.id + '/issue-notes', {
        method: 'POST',
        body: JSON.stringify({ ...note, lines: [{ contractLineId: signed.body?.lines?.[0]?.id, qty: 1 }] }),
      }, token);
      const lockedNow = await json('/api/rentals/contracts/' + unsigned.body?.id, {}, token);
      check('the same note goes through once a name is on the paper, and the contract locks with it',
        issuedNow.status === 201 && lockedNow.body?.isLocked === true && lockedNow.body?.signatoryName === 'SMOKE Délégué',
        JSON.stringify({ issue: issuedNow.status, locked: lockedNow.body?.isLocked, name: lockedNow.body?.signatoryName }));
    } else {
      console.log('SKIP  the blank-signatory refusal needs a customer of unknown kind; this database has none '
        + '(fresh seed — the rule is covered by RentalDocumentTests on SQLite)');
    }

    // ---------- the day cutoff hour (answer 7) round-trips, and is put back ----------
    // After every rental document above, so none of their charges moved with it.
    const cutoffBefore = settings.body?.dayCutoffHour ?? null;
    const settingsBody = (hour) => JSON.stringify({
      overdueMultiplier: settings.body.overdueMultiplier, depositEnabled: settings.body.depositEnabled,
      taxRate: settings.body.taxRate ?? 0, dayCutoffHour: hour,
    });
    const cutoffSet = await json('/api/rentals/settings', { method: 'PUT', body: settingsBody(cutoffBefore === 12 ? 13 : 12) }, token);
    const cutoffRead = await json('/api/rentals/settings', {}, token);
    const cutoffWanted = cutoffBefore === 12 ? 13 : 12;
    check('the day cutoff hour is written and reads back',
      cutoffSet.status === 200 && cutoffRead.body?.dayCutoffHour === cutoffWanted,
      'HTTP ' + cutoffSet.status + ' hour ' + cutoffRead.body?.dayCutoffHour);
    const cutoffBad = await json('/api/rentals/settings', { method: 'PUT', body: settingsBody(24) }, token);
    check('a cutoff hour outside the day is refused', cutoffBad.status === 400, 'HTTP ' + cutoffBad.status);
    const cutoffBack = await json('/api/rentals/settings', { method: 'PUT', body: settingsBody(cutoffBefore) }, token);
    const cutoffAfter = await json('/api/rentals/settings', {}, token);
    check('the cutoff hour is put back as it was found',
      cutoffBack.status === 200 && (cutoffAfter.body?.dayCutoffHour ?? null) === cutoffBefore,
      'hour ' + cutoffAfter.body?.dayCutoffHour);

    // Owner, 2026-10-06: a refund may name a contract for its unused period. The listing runs the
    // sums over notes and vouchers this provider has to translate.
    const refundableContracts = await json('/api/payment-vouchers/refundable-contracts?customerId=' + renter.body.id, {}, token);
    check('the contracts a refund for an unused period may name are listed',
      refundableContracts.status === 200 && Array.isArray(refundableContracts.body), 'HTTP ' + refundableContracts.status);
    const oneContract = await json('/api/payment-vouchers/refundable-contracts?contractId=' + contract.body.id, {}, token);
    check('a contract asked for by id is answered even with nothing left on it',
      oneContract.status === 200 && oneContract.body?.length === 1 && oneContract.body[0].contractId === contract.body.id
      && oneContract.body[0].stillOwed >= 0,
      'HTTP ' + oneContract.status + ' ' + JSON.stringify(oneContract.body));

    // ---------- the payment voucher (answers 26-28): raised, approved, paid ----------
    // An expense for fuel, paid in cash from the branch's box. Pending moves nothing; a short
    // box is a warning beside the paid voucher, never a refusal.
    const pv = await json('/api/payment-vouchers', {
      method: 'POST',
      body: JSON.stringify({
        branchId: depot.id, kind: 2, amount: 1500, methodKey: 'cash',
        payeeName: 'SMOKE Station Naftal', expenseCategory: 'fuel', purpose: 'Gasoil du camion ' + stamp,
      }),
    }, token);
    const pvId = pv.body?.voucher?.id;
    check('a payment voucher is raised pending', pv.status === 201 && pv.body?.voucher?.status === 1,
      'HTTP ' + pv.status + ' ' + JSON.stringify(pv.body?.voucher?.status ?? pv.body));
    check('the voucher is numbered on its own PV- series', /^PV-/.test(pv.body?.voucher?.number || ''),
      pv.body?.voucher?.number);
    const pvPayEarly = pvId ? await json('/api/payment-vouchers/' + pvId + '/pay', { method: 'POST', body: '{}' }, token) : { status: 0 };
    check('a pending voucher cannot be paid before it is approved', pvPayEarly.status === 409, 'HTTP ' + pvPayEarly.status);
    const pvApproved = pvId ? await json('/api/payment-vouchers/' + pvId + '/approve', { method: 'POST', body: '{}' }, token) : { status: 0 };
    check('a manager approves the voucher', pvApproved.status === 200 && pvApproved.body?.voucher?.status === 2
      && !!pvApproved.body?.voucher?.approvedAtUtc, 'HTTP ' + pvApproved.status);
    const pvPaid = pvId ? await json('/api/payment-vouchers/' + pvId + '/pay', { method: 'POST', body: '{}' }, token) : { status: 0 };
    check('the approved voucher is paid', pvPaid.status === 200 && pvPaid.body?.voucher?.status === 3
      && !!pvPaid.body?.voucher?.paidAtUtc, 'HTTP ' + pvPaid.status + ' ' + (pvPaid.body?.warning || ''));
    const pvList = await json('/api/payment-vouchers?status=3', {}, token);
    const pvRow = (pvList.body || []).find((v) => v.id === pvId);
    check('the voucher list shows it paid', !!pvRow && pvRow.status === 3 && pvRow.number === pv.body?.voucher?.number,
      'HTTP ' + pvList.status + ' ' + JSON.stringify(pvRow ? { n: pvRow.number, s: pvRow.status } : null));
    const pvEntries = await json(`/api/ledger/entries?sourceType=payment-voucher&${q}`, {}, token);
    const pvEntry = (pvEntries.body || []).find((e) => e.sourceId === pvId);
    check('paying it posts one balanced entry, the expense against the cash',
      !!pvEntry && Math.abs(pvEntry.lines.reduce((t, l) => t + l.debit - l.credit, 0)) < 0.001
      && pvEntry.lines.some((l) => l.accountCode === '530' && l.credit > 0),
      pvEntry ? pvEntry.lines.map((l) => l.accountCode).join(' ') : 'no entry');

    // ---------- phase 5: invoices, partial payments, credit notes, statement, ageing ----------
    // The customers here are contractors on account with million-dinar limits, so this is the
    // part the cash flow actually runs on. Every check below is about money that must not move
    // when paper arrives, or must not move twice when a document is invoiced.

    const due = (offset) => new Date(Date.now() + offset * 86400e3).toISOString();

    // Since 2026-10-11 (486350c) an invoice is raised over a document and never typed in: a
    // request with lines and no source is refused (invoice_needs_source). So each invoice here
    // bills a sale the till pushed on account — the sale puts the debt on the customer, the
    // invoice is its paper — priced as the till prices it: the figure before tax, the tax on top.
    const typed = await json('/api/receivables/invoices', {
      method: 'POST',
      body: JSON.stringify({
        customerId: onAccount.id, branchId: depot.id, dueDateUtc: due(30), notes: 'SMOKE',
        lines: [{ sku: 'SMOKE-0', name: 'Typed', qty: 1, unitPrice: 100, taxRate: 19 }],
      }),
    }, token);
    check('an invoice typed in by hand is refused: it is raised over a sale or a rental note',
      typed.status === 400 && typed.body?.code === 'invoice_needs_source',
      'HTTP ' + typed.status + ' ' + typed.body?.code);

    const loose = (products.body || []).find((p) => !p.trackStock && !p.isDeleted) || tracked;
    let creditSeq = Date.now();
    const cents = (v) => Math.round(v * 100) / 100;
    const creditSale = async (soldAtUtc, lines) => {
      const priced = lines.map((l) => {
        const net = cents(l.qty * l.unitPrice);
        const tax = cents(net * l.taxRate / 100);
        return {
          id: crypto.randomUUID(), productId: loose.id, sku: l.sku, nameSnapshot: l.name, qty: l.qty,
          unitPrice: l.unitPrice, taxRate: l.taxRate, lineDiscount: 0, lineTotalTtc: cents(net + tax), lineTaxAmount: tax,
        };
      });
      const subTotalHt = cents(priced.reduce((t, l) => t + l.lineTotalTtc - l.lineTaxAmount, 0));
      const taxTotal = cents(priced.reduce((t, l) => t + l.lineTaxAmount, 0));
      const grandTotalTtc = cents(subTotalHt + taxTotal);
      const sale = {
        id: crypto.randomUUID(), reference: 'SMOKE-CR-' + creditSeq.toString(36).toUpperCase(), localSeq: creditSeq++,
        soldAtUtc, customerId: onAccount.id, cashierUserId: login.body.user.id, cashierName: login.body.user.fullName,
        subTotalHt, taxTotal, discountTotal: 0, grandTotalTtc, paidAmount: grandTotalTtc, changeAmount: 0,
        paymentMethod: 'Credit', status: 'Completed', refundOfSaleId: null, note: 'provider smoke', taxMode: 'taxed',
        lines: priced,
        payments: [{ id: crypto.randomUUID(), method: 'credit', amount: grandTotalTtc, reference: null, methodKey: 'credit' }],
      };
      const pushed = await json('/api/sync/push', { method: 'POST', body: JSON.stringify({ deviceCode: 'C01', sales: [sale] }) }, token);
      return { sale, ok: pushed.status === 200 && pushed.body?.accepted === 1, pushed };
    };

    const concrete = await creditSale(new Date().toISOString(), [
      { sku: 'SMOKE-1', name: 'Béton C25/30', qty: 10, unitPrice: 12000, taxRate: 19 },
      { sku: 'SMOKE-2', name: 'Transport', qty: 1, unitPrice: 9999.99, taxRate: 19 },
    ]);
    check('a sale on account is pushed from the till', concrete.ok,
      'HTTP ' + concrete.pushed.status + ' ' + JSON.stringify(concrete.pushed.body?.results?.[0] ?? concrete.pushed.body).slice(0, 160));
    const invoiced = await json('/api/receivables/invoices', {
      method: 'POST',
      body: JSON.stringify({ saleId: concrete.sale.id, dueDateUtc: due(30), notes: 'SMOKE' }),
    }, token);
    check('an invoice can be raised over that sale', invoiced.status === 201, 'HTTP ' + invoiced.status);

    const invoice = invoiced.body?.invoice;
    // §8 Q12, accepted: the paper freezes who it was issued to. A reprint must agree with the
    // first print whatever the customer row says later, so the name is copied at issue.
    check('a new invoice carries the buyer frozen at issue',
      !!invoice && invoice.buyerName === onAccount.fullName
      && (invoice.buyerTaxId ?? null) === (onAccount.taxId ?? null)
      && (invoice.buyerAddress ?? null) === (onAccount.address ?? null),
      JSON.stringify({ buyer: invoice?.buyerName, customer: onAccount.fullName, taxId: invoice?.buyerTaxId }));
    // Tax on each rate's own total, not line by line: (120 000 + 9 999.99) × 19%.
    const expectedTax = Math.round(129999.99 * 0.19 * 100) / 100;
    check('tax is charged on the total per rate, not line by line',
      invoice && Math.abs(invoice.taxTotal - expectedTax) < 0.01, `${invoice?.taxTotal} vs ${expectedTax}`);
    check('the invoice carries a due date and is open',
      invoice?.status === 'Open' && !!invoice?.dueDateUtc && invoice?.outstanding === invoice?.grandTotalTtc,
      JSON.stringify({ status: invoice?.status, outstanding: invoice?.outstanding }));
    check('the invoice bills exactly what the sale put on the account, and says it came from a sale',
      invoice?.origin === 'Sale' && Math.abs((invoice?.grandTotalTtc ?? 0) - concrete.sale.grandTotalTtc) < 0.01,
      JSON.stringify({ origin: invoice?.origin, invoiced: invoice?.grandTotalTtc, sold: concrete.sale.grandTotalTtc }));

    // ⚠ The money was posted once, by the sale; the invoice over it must post nothing.
    const saleEntries = await json(`/api/ledger/entries?sourceType=sale&sourceId=${concrete.sale.id}`, {}, token);
    const saleEntry = (saleEntries.body || [])[0];
    check('the sale posted one balanced entry',
      !!saleEntry && Math.abs(saleEntry.lines.reduce((t, l) => t + l.debit - l.credit, 0)) < 0.001);
    if (saleEntry) {
      const codes = saleEntry.lines.map((l) => l.accountCode);
      check('the customer is billed the gross and only the net is income',
        saleEntry.lines.some((l) => l.accountCode === '411' && Math.abs(l.debit - invoice.grandTotalTtc) < 0.01)
        && saleEntry.lines.some((l) => l.accountCode === '700' && l.credit > 0)
        && codes.includes('4457'),
        codes.join(' '));
    }
    const invoiceEntries = await json(`/api/ledger/entries?sourceType=invoice&sourceId=${invoice?.id}`, {}, token);
    check('invoicing the sale posts nothing a second time',
      invoiceEntries.status === 200 && (invoiceEntries.body || []).length === 0,
      'entries ' + (invoiceEntries.body || []).length);

    // ⚠ The rule §5.4 is explicit about and this phase exists to protect.
    const cheque = (methods.body || []).find((m) => m.requiresConfirmation && m.behavior === 'Generic')
      || created.body;
    const paper = await json('/api/receivables/receipts', {
      method: 'POST',
      body: JSON.stringify({
        customerId: onAccount.id, branchId: depot.id, amount: invoice.grandTotalTtc,
        methodKey: cheque.key, instrumentNumber: 'SMOKE-CHQ', instrumentBank: 'BEA',
        instrumentDueDate: due(15),
        allocations: [{ invoiceId: invoice.id, amount: invoice.grandTotalTtc }],
      }),
    }, token);
    check('a cheque can be taken against an invoice', paper.status === 201, 'HTTP ' + paper.status);
    check('a cheque taken at the counter is pending, not paid', paper.body?.status === 'Pending');

    const stillOwed = await json('/api/receivables/invoices/' + invoice.id, {}, token);
    check('a pending cheque does not reduce the debt',
      stillOwed.body?.outstanding === invoice.grandTotalTtc && stillOwed.body?.status === 'Open',
      JSON.stringify({ outstanding: stillOwed.body?.outstanding, status: stillOwed.body?.status }));
    check('what a pending cheque speaks for is visible without the invoice looking paid',
      Math.abs((stillOwed.body?.claimed ?? 0) - invoice.grandTotalTtc) < 0.01
      && (stillOwed.body?.received ?? 0) === 0,
      JSON.stringify({ claimed: stillOwed.body?.claimed, received: stillOwed.body?.received }));

    const noEntryYet = await json(`/api/ledger/entries?sourceType=customer-receipt&sourceId=${paper.body?.id}`, {}, token);
    check('a pending cheque reaches no account at all', (noEntryYet.body || []).length === 0);

    const cleared = await json('/api/receivables/receipts/' + paper.body.id + '/settle', {
      method: 'POST',
      body: JSON.stringify({ status: 'Confirmed', note: 'SMOKE — encaissé' }),
    }, token);
    check('a cheque can be confirmed', cleared.status === 200, 'HTTP ' + cleared.status);

    const settledInvoice = await json('/api/receivables/invoices/' + invoice.id, {}, token);
    check('confirming the cheque is what settles the invoice',
      settledInvoice.body?.outstanding === 0 && settledInvoice.body?.status === 'Settled',
      JSON.stringify({ outstanding: settledInvoice.body?.outstanding, status: settledInvoice.body?.status }));

    const receiptEntries = await json(`/api/ledger/entries?sourceType=customer-receipt&sourceId=${paper.body.id}`, {}, token);
    const receiptEntry = (receiptEntries.body || [])[0];
    check('a cleared cheque is money at the bank and never revenue',
      !!receiptEntry
      && receiptEntry.lines.some((l) => l.accountCode === '512' && l.debit > 0)
      && receiptEntry.lines.some((l) => l.accountCode === '411' && l.credit > 0)
      && !receiptEntry.lines.some((l) => l.accountType === 'Income'),
      receiptEntry ? receiptEntry.lines.map((l) => l.accountCode).join(' ') : 'no entry');

    // A second invoice, paid in part with cash, then partly credited — over a delivery sold on
    // account two and a half months ago, which a till offline that long is allowed to push.
    const gravelSale = await creditSale(due(-75), [
      { sku: 'SMOKE-3', name: 'Gravier', qty: 20, unitPrice: 5000, taxRate: 0 },
    ]);
    check('an old sale on account is still recorded when it reaches the server', gravelSale.ok,
      'HTTP ' + gravelSale.pushed.status + ' ' + JSON.stringify(gravelSale.pushed.body?.results?.[0] ?? gravelSale.pushed.body).slice(0, 160));
    const gravel = await json('/api/receivables/invoices', {
      method: 'POST',
      body: JSON.stringify({
        saleId: gravelSale.sale.id, issuedAtUtc: due(-75), dueDateUtc: due(-45), notes: 'SMOKE overdue',
      }),
    }, token);
    const overdue = gravel.body?.invoice;
    // Raised today for a delivery two months ago, on terms that have already lapsed. An invoice
    // whose due date preceded its own issue date would be a typo, and is refused.
    check('an invoice can be back-dated and be overdue the day it is raised',
      gravel.status === 201, 'HTTP ' + gravel.status);

    const partly = await json('/api/receivables/receipts', {
      method: 'POST',
      body: JSON.stringify({
        customerId: onAccount.id, branchId: depot.id, amount: 30000, methodKey: 'cash',
        allocations: [{ invoiceId: overdue.id, amount: 30000 }],
      }),
    }, token);
    check('a customer can pay part of an invoice in cash',
      partly.status === 201 && partly.body?.status === 'Confirmed', 'HTTP ' + partly.status);

    const afterPart = await json('/api/receivables/invoices/' + overdue.id, {}, token);
    check('a partial payment leaves the rest outstanding',
      afterPart.body?.outstanding === 70000 && afterPart.body?.status === 'PartlyPaid',
      JSON.stringify({ outstanding: afterPart.body?.outstanding, status: afterPart.body?.status }));
    check('an overdue invoice reports how late it is',
      afterPart.body?.daysOverdue >= 44 && afterPart.body?.ageingBucket === 'Days31To60',
      JSON.stringify({ days: afterPart.body?.daysOverdue, bucket: afterPart.body?.ageingBucket }));

    const creditNote = await json('/api/receivables/invoices/' + overdue.id + '/credit-notes', {
      method: 'POST',
      body: JSON.stringify({
        reason: 'SMOKE — 4 t non livrées',
        lines: [{ invoiceLineId: overdue.lines[0].id, qty: 4 }],
      }),
    }, token);
    check('an invoice is reversed with a credit note', creditNote.status === 201, 'HTTP ' + creditNote.status);
    check('the credit note gives back what that quantity was billed',
      Math.abs((creditNote.body?.grandTotalTtc ?? 0) - 20000) < 0.01, 'total ' + creditNote.body?.grandTotalTtc);

    // The printed credit note reads one document by id (docs/07 §4.4); until this route the
    // Location the POST returned led nowhere.
    const noteRead = await json('/api/receivables/credit-notes/' + creditNote.body?.id, {}, token);
    check('a credit note reads back by id with the invoice it corrects',
      noteRead.status === 200 && noteRead.body?.id === creditNote.body?.id
      && noteRead.body?.invoiceNumber === overdue.number
      && Math.abs((noteRead.body?.grandTotalTtc ?? 0) - 20000) < 0.01
      && Array.isArray(noteRead.body?.lines) && noteRead.body.lines.length === 1,
      'HTTP ' + noteRead.status + ' corrects ' + noteRead.body?.invoiceNumber + ' (invoice ' + overdue.number + ')');
    const noSuchNote = await json('/api/receivables/credit-notes/00000000-0000-0000-0000-000000000001', {}, token);
    check('an unknown credit note is a 404, not an empty document', noSuchNote.status === 404, 'HTTP ' + noSuchNote.status);

    const unreasoned = await json('/api/receivables/invoices/' + overdue.id + '/credit-notes', {
      method: 'POST',
      body: JSON.stringify({ reason: '   ' }),
    }, token);
    check('a credit note is refused without a reason', unreasoned.status === 400, 'HTTP ' + unreasoned.status);

    // An invoice over a rental note, which has already posted its own revenue and receivable.
    const beforeRentalInvoice = await json(
      `/api/receivables/statement?customerId=${onAccount.id}&from=${encodeURIComponent(due(-365))}&to=${encodeURIComponent(due(1))}`,
      {}, token);
    const rentalInvoice = await json('/api/receivables/invoices', {
      method: 'POST',
      body: JSON.stringify({ dueDateUtc: due(30), issueNoteId: issue.body?.id }),
    }, token);
    check('a rental issue note can be invoiced', rentalInvoice.status === 201, 'HTTP ' + rentalInvoice.status);
    check('the invoice bills exactly what the note put on the account',
      Math.abs((rentalInvoice.body?.invoice?.grandTotalTtc ?? 0) - 18000) < 0.01,
      'total ' + rentalInvoice.body?.invoice?.grandTotalTtc);
    check('a rental line is billed as rent, not as merchandise',
      rentalInvoice.body?.invoice?.lines?.[0]?.incomeAccountCode === '706');

    const afterRentalInvoice = await json(
      `/api/receivables/statement?customerId=${onAccount.id}&from=${encodeURIComponent(due(-365))}&to=${encodeURIComponent(due(1))}`,
      {}, token);
    // ⚠ The trap: the hire reached 411 when the goods went out. Billing it again would make the
    // contractor owe it twice.
    check('invoicing a document that has already posted bills nothing a second time',
      typeof beforeRentalInvoice.body?.closingBalance === 'number'
      && beforeRentalInvoice.body.closingBalance === afterRentalInvoice.body?.closingBalance,
      `${beforeRentalInvoice.body?.closingBalance} -> ${afterRentalInvoice.body?.closingBalance}`);

    const twice = await json('/api/receivables/invoices', {
      method: 'POST',
      body: JSON.stringify({ dueDateUtc: due(30), issueNoteId: issue.body?.id }),
    }, token);
    check('the same document cannot be invoiced twice', twice.status === 409, 'HTTP ' + twice.status);

    // The statement, and what it has to agree with.
    const statement = afterRentalInvoice.body;
    check('the statement lists every movement and closes where the rows do',
      Array.isArray(statement?.rows) && statement.rows.length > 0
      && Math.abs(statement.rows[statement.rows.length - 1].balance - statement.closingBalance) < 0.01,
      `rows ${statement?.rows?.length}`);
    check('the statement opens where it opened and adds up to where it closes',
      Math.abs(statement.openingBalance + statement.totalDebit - statement.totalCredit - statement.closingBalance) < 0.01,
      `${statement.openingBalance} + ${statement.totalDebit} - ${statement.totalCredit} vs ${statement.closingBalance}`);
    // ⚠ The property the whole phase is for: the books and the balance the business chases are
    // the same number, with uncleared paper accounted for separately rather than netted off.
    check('the statement agrees with the balance the business carries',
      statement.balanceDifference === 0,
      `recorded ${statement.recordedBalance} vs trade ${statement.closingBalance} + paper ${statement.pendingNotes}`);
    check('debt with an invoice and debt without one are both stated',
      Math.abs(statement.invoicedOutstanding + statement.uninvoicedBalance - statement.closingBalance) < 0.01,
      `${statement.invoicedOutstanding} + ${statement.uninvoicedBalance} vs ${statement.closingBalance}`);
    check('every statement row leads back to a document',
      statement.rows.every((r) => !!r.documentNumber || !!r.invoiceNumber),
      statement.rows.filter((r) => !r.documentNumber && !r.invoiceNumber).map((r) => r.sourceType).join(' '));

    const ageing = await json(`/api/receivables/ageing?customerId=${onAccount.id}`, {}, token);
    const aged = (ageing.body?.customers || [])[0];
    check('the ageing report slices the same debt by age',
      !!aged && Math.abs(aged.current + aged.days1To30 + aged.days31To60 + aged.days61To90 + aged.over90 - aged.total) < 0.01,
      JSON.stringify(aged));
    check('the ageing report reads the same ledger balance as the statement',
      aged && Math.abs(aged.ledgerBalance - statement.closingBalance) < 0.01,
      `${aged?.ledgerBalance} vs ${statement.closingBalance}`);
    check('what the ageing report cannot see is a stated figure',
      aged && Math.abs(aged.ledgerBalance - (aged.total - aged.creditBalance) - aged.uninvoiced) < 0.01,
      JSON.stringify({ ledger: aged?.ledgerBalance, total: aged?.total, uninvoiced: aged?.uninvoiced }));
    check('an overdue invoice lands in a band and not in "current"',
      aged && aged.days31To60 > 0 && aged.oldestDaysOverdue >= 44,
      JSON.stringify({ band: aged?.days31To60, oldest: aged?.oldestDaysOverdue }));

    const overdueOnly = await json('/api/receivables/invoices?overdue=true', {}, token);
    check('the invoice list can be narrowed to what is actually late',
      Array.isArray(overdueOnly.body) && overdueOnly.body.every((i) => i.daysOverdue > 0 && i.outstanding > 0),
      'count ' + (overdueOnly.body || []).length);

    const finalTrial = await json(`/api/ledger/reports/trial-balance?${q}`, {}, token);
    check('the books still balance once renting has posted',
      finalTrial.body?.balanced === true,
      `${finalTrial.body?.totalDebit} vs ${finalTrial.body?.totalCredit}`);
  }

  // ---------- phase 6: personnel and payroll ----------
  // A month's wages is a chain too, and every link is somewhere a provider or a wiring
  // difference shows: decimals the engine multiplies and SQLite stores as TEXT, a run created
  // inside the subscription filter's own transaction, a document number taken from CountAsync+1
  // in that same scope, an accrual that must balance to the centime, and an advance that has to
  // come back out of the next run's net.
  //
  // It runs on two months of its own, low in the past and chosen by looking at what is already
  // there, so re-running the script never meets "a live run already covers this month".
  if (depot) {
    const payBox = (await json('/api/treasury/cash-boxes', {}, token)).body
      ?.find((b) => b.branchId === depot.id);

    const settingsRead = await json('/api/hr/settings', {}, token);
    check('the payroll figures read back for the run to be computed against',
      settingsRead.status === 200 && !!settingsRead.body?.settings, 'HTTP ' + settingsRead.status);
    // Part A ships placeholders marked unconfirmed. Whether they still are is the company's
    // business; what must hold is that the run's own mark agrees with them. Since f0fd3ee
    // (2026-10-21) a new run leaves CNAS and IRG off the slip, so the only unconfirmed figures it
    // still uses are the employer levies: it is provisional when one of those is active.
    const unconfirmed = settingsRead.body?.settings?.confirmed !== true;
    const leviesInPlay = (settingsRead.body?.settings?.employerLevies || []).some((l) => l.isActive);
    const provisional = unconfirmed && leviesInPlay;

    // One employee, reused across runs of this script: a fresh one every time would leave a
    // payroll growing by a person a day.
    const smokeName = 'SMOKE Paie Employe';
    const existing = await json(
      `/api/hr/employees?branchId=${depot.id}&q=${encodeURIComponent(smokeName)}`, {}, token);
    check('the employee directory answers a paged search',
      existing.status === 200 && Array.isArray(existing.body?.rows) && typeof existing.body?.total === 'number',
      'HTTP ' + existing.status);

    let employee = (existing.body?.rows || []).find((e) => e.fullName === smokeName);
    let hiredNow = false;
    if (!employee) {
      const madeEmployee = await json('/api/hr/employees', {
        method: 'POST',
        body: JSON.stringify({
          fullName: smokeName, branchId: depot.id,
          payBasis: 'Monthly', baseRate: 48000, payoutMethod: 'Cash',
          hiredOn: '2019-01-01', isActive: true,
          // Declared explicitly: if the company captures time from the clock, a typed sheet row
          // is refused, and this script has no badge to swipe.
          attendanceModeOverride: 'Declared',
          jobTitle: 'SMOKE', notes: 'provider-smoke.cjs',
        }),
      }, token);
      check('an employee can be taken on', madeEmployee.status === 201, 'HTTP ' + madeEmployee.status);
      employee = madeEmployee.body;
      hiredNow = madeEmployee.status === 201;
    }
    check('the employee carries a number the payslip can print',
      !!employee?.id && typeof employee?.number === 'string' && employee.number.length > 0,
      employee?.number);
    // A new hire's number is drawn like a customer's (owner, 2026-10-05): Emp-, the location's three
    // digits, YYMM, a sequence — Emp-3612610001. One hired by an earlier run keeps the EMP-000001 it
    // was given, so only a hire made by this run is held to the new shape.
    if (hiredNow) {
      check('a new hire is given a number the server drew, at one of the locations',
        /^Emp-[1-9]\d{2}\d{4}\d{3,}$/.test(employee.number) && branchCodes.has(locationOf(employee.number)),
        employee.number);
    }

    // Two free consecutive months, from an anchor far enough back to meet no real payroll.
    const runsSoFar = await json(`/api/hr/payroll/runs?branchId=${depot.id}&take=500`, {}, token);
    check('the run list reads back for this branch', runsSoFar.status === 200, 'HTTP ' + runsSoFar.status);
    const taken = new Set((runsSoFar.body || [])
      .filter((r) => r.status !== 'Reversed')
      .map((r) => `${r.periodYear}-${r.periodMonth}`));
    let first = null;
    for (let i = 0; i < 480 && !first; i++) {
      const y = 2019 + Math.floor(i / 12);
      const m = (i % 12) + 1;
      const nextY = m === 12 ? y + 1 : y;
      const nextM = m === 12 ? 1 : m + 1;
      if (!taken.has(`${y}-${m}`) && !taken.has(`${nextY}-${nextM}`)) {
        first = { year: y, month: m, next: { year: nextY, month: nextM } };
      }
    }
    check('two consecutive months are free for this branch', !!first,
      first ? `${first.year}-${first.month} and ${first.next.year}-${first.next.month}` : 'none in 40 years');

    // One month of wages, from the sheet to the ledger.
    const payMonth = async (period, label) => {
      const declared = await json('/api/hr/attendance/sheet', {
        method: 'PUT',
        body: JSON.stringify({
          branchId: depot.id, periodYear: period.year, periodMonth: period.month,
          rows: [{
            employeeId: employee.id,
            daysPresent: 22, paidLeaveDays: 2, unpaidAbsenceDays: 1, overtime: [],
          }],
        }),
      }, token);
      check(`${label}: the foreman's sheet is accepted`, declared.status === 200, 'HTTP ' + declared.status);
      const declaredRow = (declared.body?.rows || []).find((r) => r.employeeId === employee.id);
      check(`${label}: the sheet answers the row it was sent`,
        declaredRow?.daysPresent === 22 && declaredRow?.paidLeaveDays === 2
        && declaredRow?.unpaidAbsenceDays === 1 && declaredRow?.source === 'Declared',
        JSON.stringify(declaredRow && [declaredRow.daysPresent, declaredRow.source]));

      const reread = await json(
        `/api/hr/attendance/sheet?branchId=${depot.id}&year=${period.year}&month=${period.month}`, {}, token);
      const rereadRow = (reread.body?.rows || []).find((r) => r.employeeId === employee.id);
      check(`${label}: what was declared is what reads back`,
        rereadRow?.daysPresent === 22 && reread.body?.locked === false,
        `${rereadRow?.daysPresent} days, locked ${reread.body?.locked}`);

      const opened = await json('/api/hr/payroll/runs', {
        method: 'POST',
        body: JSON.stringify({ branchId: depot.id, periodYear: period.year, periodMonth: period.month }),
      }, token);
      check(`${label}: the month opens as a run`, opened.status === 201, 'HTTP ' + opened.status);
      const runId = opened.body?.id;
      check(`${label}: the run is numbered inside the write scope`,
        typeof opened.body?.number === 'string' && opened.body.number.startsWith('RUN-'), opened.body?.number);

      const twice = await json('/api/hr/payroll/runs', {
        method: 'POST',
        body: JSON.stringify({ branchId: depot.id, periodYear: period.year, periodMonth: period.month }),
      }, token);
      check(`${label}: a second live run for the same month is refused`, twice.status === 409,
        'HTTP ' + twice.status);

      const computed = await json(`/api/hr/payroll/runs/${runId}/compute`, { method: 'POST' }, token);
      check(`${label}: the run computes`, computed.status === 200 && computed.body?.run?.status === 'Locked',
        'HTTP ' + computed.status + ' ' + computed.body?.run?.status);
      // A code, never an English sentence: the screen says it in the reader's own language.
      check(`${label}: a provisional run says so as a code, and agrees with the settings`,
        (computed.body?.run?.isProvisional === true) === provisional
        && (computed.body?.warning ?? null) === (provisional ? 'settings_unconfirmed' : null),
        `provisional ${computed.body?.run?.isProvisional} (expected ${provisional}), warning ${computed.body?.warning}`);

      const afterCompute = await json(
        `/api/hr/attendance/sheet?branchId=${depot.id}&year=${period.year}&month=${period.month}`, {}, token);
      check(`${label}: computing locks the month and names the run that did it`,
        afterCompute.body?.locked === true && afterCompute.body?.runNumber === opened.body?.number,
        `locked ${afterCompute.body?.locked} by ${afterCompute.body?.runNumber}`);

      const blocked = await json('/api/hr/attendance/sheet', {
        method: 'PUT',
        body: JSON.stringify({
          branchId: depot.id, periodYear: period.year, periodMonth: period.month,
          rows: [{ employeeId: employee.id, daysPresent: 20, paidLeaveDays: 0, unpaidAbsenceDays: 0, overtime: [] }],
        }),
      }, token);
      check(`${label}: attendance under a locked run is refused with a code and the run's name`,
        blocked.status === 409 && blocked.body?.code === 'periodLocked' && !!blocked.body?.runNumber,
        'HTTP ' + blocked.status + ' ' + blocked.body?.code);

      const lines = await json(`/api/hr/payroll/runs/${runId}/lines?take=100`, {}, token);
      const line = (lines.body?.lines || []).find((l) => l.employeeId === employee.id);
      check(`${label}: the payslip carries its own line id, net and paid figure`,
        !!line?.id && typeof line?.net === 'number' && typeof line?.paidAmount === 'number',
        line ? `net ${line.net}` : 'no line');
      check(`${label}: the payslip's days are the ones declared`,
        line?.daysPresent === 22 && line?.attendanceSource === 'Declared',
        `${line?.daysPresent} days from ${line?.attendanceSource}`);

      const detail = await json(`/api/hr/payroll/runs/${runId}`, {}, token);
      check(`${label}: the detail states the entry approving will write, in account codes`,
        Array.isArray(detail.body?.posting) && detail.body.posting.length > 0
        && detail.body.posting.every((p) => typeof p.accountCode === 'string'),
        'lines ' + (detail.body?.posting || []).length);
      const willDebit = (detail.body?.posting || []).reduce((t, p) => t + (p.debit || 0), 0);
      const willCredit = (detail.body?.posting || []).reduce((t, p) => t + (p.credit || 0), 0);
      check(`${label}: the entry it promises balances before anything is posted`,
        Math.abs(willDebit - willCredit) < 0.005, `${willDebit} vs ${willCredit}`);

      const approved = await json(`/api/hr/payroll/runs/${runId}/approve`, { method: 'POST' }, token);
      check(`${label}: the run is approved and posted`,
        approved.status === 200 && approved.body?.run?.status === 'Approved' && !!approved.body?.run?.ledgerEntryId,
        'HTTP ' + approved.status + ' ' + approved.body?.run?.status);

      const entry = await json('/api/ledger/entries/' + approved.body?.run?.ledgerEntryId, {}, token);
      const debits = (entry.body?.lines || []).reduce((t, l) => t + (l.debit || 0), 0);
      const credits = (entry.body?.lines || []).reduce((t, l) => t + (l.credit || 0), 0);
      check(`${label}: the wage accrual balances in the books`,
        entry.status === 200 && (entry.body?.lines || []).length > 0 && Math.abs(debits - credits) < 0.005,
        `${debits} vs ${credits}`);
      check(`${label}: the accrual raises the wages payable the payment will clear`,
        (entry.body?.lines || []).some((l) => l.accountCode === '421' && (l.credit || 0) > 0),
        (entry.body?.lines || []).map((l) => l.accountCode).join(' '));

      return { runId, number: opened.body?.number, run: approved.body?.run, line };
    };

    const monthOne = await payMonth(first, 'month one');

    // The advance goes out between the two runs, which is the only moment it can be recovered
    // by the second rather than the first.
    const advanceAmount = 5000;
    const granted = await json('/api/hr/advances', {
      method: 'POST',
      body: JSON.stringify({
        employeeId: employee.id, amount: advanceAmount, payoutMethod: 'Cash',
        cashBoxId: payBox ? payBox.id : null, reference: 'SMOKE-ADV-' + stamp,
      }),
    }, token);
    check('an advance can be granted against next month\'s wages',
      granted.status === 201 && granted.body?.advance?.status === 'Open',
      'HTTP ' + granted.status + ' ' + granted.body?.advance?.status);
    check('the advance is numbered and states what is still to recover',
      typeof granted.body?.advance?.number === 'string' && granted.body.advance.number.startsWith('ADV-')
      && granted.body?.advance?.outstanding === advanceAmount,
      `${granted.body?.advance?.number} outstanding ${granted.body?.advance?.outstanding}`);

    const monthTwo = await payMonth(first.next, 'month two');

    check('the next run recovers the advance from the net',
      monthTwo.line?.advanceRecovered === advanceAmount,
      'recovered ' + monthTwo.line?.advanceRecovered);

    const advanceAfter = await json('/api/hr/advances/' + granted.body?.advance?.id, {}, token);
    check('an advance recovered in full is closed and names the run that took it',
      advanceAfter.body?.status === 'Recovered' && advanceAfter.body?.outstanding === 0
      && advanceAfter.body?.recoveredInRunNumber === monthTwo.number,
      `${advanceAfter.body?.status} by ${advanceAfter.body?.recoveredInRunNumber}`);

    // Wages, paid out of the branch's own box. The payslip is named explicitly rather than
    // paying "everyone still owed": this branch may carry staff this script never created, and
    // a figure checked against the whole run would then be checking somebody else's wages too.
    const boxBefore = (await json('/api/treasury/cash-boxes', {}, token)).body
      ?.find((b) => b.id === payBox?.id);
    const paid = await json(`/api/hr/payroll/runs/${monthTwo.runId}/payments`, {
      method: 'POST',
      body: JSON.stringify({
        runId: monthTwo.runId, payoutMethod: 'Cash',
        cashBoxId: payBox ? payBox.id : null, reference: 'SMOKE-WP-' + stamp,
        lines: [{ runLineId: monthTwo.line.id, amount: monthTwo.line.net }],
      }),
    }, token);
    check('the wages are paid against the approved run',
      paid.status === 201 && typeof paid.body?.payment?.payment?.number === 'string'
      && paid.body.payment.payment.number.startsWith('WP-'),
      'HTTP ' + paid.status + ' ' + paid.body?.payment?.payment?.number);
    const paidAmount = paid.body?.payment?.payment?.amount;
    check('what was handed over is the net the payslip states',
      paidAmount === monthTwo.line?.net, `${paidAmount} against net ${monthTwo.line?.net}`);

    const boxAfter = (await json('/api/treasury/cash-boxes', {}, token)).body
      ?.find((b) => b.id === payBox?.id);
    if (boxBefore && boxAfter) {
      check('the box is lighter by exactly the wages that left it',
        Math.abs((boxBefore.balance - boxAfter.balance) - paidAmount) < 0.005,
        `${boxBefore.balance} -> ${boxAfter.balance}`);
    }

    const twicePaid = await json(`/api/hr/payroll/runs/${monthTwo.runId}/payments`, {
      method: 'POST',
      body: JSON.stringify({
        runId: monthTwo.runId, payoutMethod: 'Cash', cashBoxId: payBox ? payBox.id : null,
        lines: [{ runLineId: monthTwo.line.id, amount: monthTwo.line.net }],
      }),
    }, token);
    check('a payslip already settled cannot be paid a second time', twicePaid.status === 400,
      'HTTP ' + twicePaid.status);

    const linesAfter = await json(`/api/hr/payroll/runs/${monthTwo.runId}/lines?take=100`, {}, token);
    const settledLine = (linesAfter.body?.lines || []).find((l) => l.employeeId === employee.id);
    check('the payslip records what its holder was actually handed',
      settledLine?.paidAmount === monthTwo.line?.net, `paid ${settledLine?.paidAmount}`);

    const settled = await json(`/api/hr/payroll/runs/${monthTwo.runId}`, {}, token);
    check('the run counts what has gone out against it',
      settled.body?.run?.paidTotal >= paidAmount, 'paidTotal ' + settled.body?.run?.paidTotal);
    // Paid is the state of a run with nothing left owing, so it only follows when this employee
    // is the whole branch.
    check('a run whose every payslip is settled becomes Paid',
      settled.body?.run?.status === (settled.body?.run?.headCount === 1 ? 'Paid' : 'Approved'),
      `${settled.body?.run?.status} over ${settled.body?.run?.headCount} payslip(s)`);

    // What is still withheld, read from the ledger lines rather than from a column beside them.
    const liabilities = await json(`/api/hr/payroll/liabilities?branchId=${depot.id}`, {}, token);
    check('the statutory liabilities are reported per account',
      liabilities.status === 200 && Array.isArray(liabilities.body)
      && liabilities.body.some((l) => ['431', '442', '447'].includes(l.accountCode)),
      (liabilities.body || []).map((l) => l.accountCode).join(' '));
    const social = (liabilities.body || []).find((l) => l.accountCode === '431');
    if (social && social.outstanding > 0) {
      const handed = await json('/api/hr/payroll/statutory-payments', {
        method: 'POST',
        body: JSON.stringify({
          branchId: depot.id, accountCode: '431', amount: 100, payoutMethod: 'Bank',
          periodYear: first.next.year, periodMonth: first.next.month, reference: 'SMOKE-ST-' + stamp,
        }),
      }, token);
      check('paying the social fund is recorded and posted',
        handed.status === 201 && !!handed.body?.payment?.ledgerEntryId, 'HTTP ' + handed.status);

      const after = await json(`/api/hr/payroll/liabilities?branchId=${depot.id}`, {}, token);
      const now = (after.body || []).find((l) => l.accountCode === '431');
      check('what was handed over comes off what is still owed',
        Math.abs((social.outstanding - now.outstanding) - 100) < 0.005,
        `${social.outstanding} -> ${now?.outstanding}`);
    }

    const payrollTrial = await json(`/api/ledger/reports/trial-balance?${q}`, {}, token);
    check('the books still balance once payroll has posted',
      payrollTrial.body?.balanced === true,
      `${payrollTrial.body?.totalDebit} vs ${payrollTrial.body?.totalCredit}`);
  }

  // ---------- printed documents: the company's terms live beside the store, never on it ----------
  // docs/07 §4.3: DocumentTexts/Email/Website/BankDetails are Admin-owned and read by whoever
  // prints. They are NOT on StoreDto, and the PUT must not bump Store.Version — a bump would
  // make every till re-pull a store row whose replicated fields did not change.
  const storeBefore = await json('/api/catalog/store', {}, token);
  const docsBefore = await json('/api/catalog/store/documents', {}, token);
  check('the printed-document texts read back',
    docsBefore.status === 200 && docsBefore.body && typeof docsBefore.body.documentTexts === 'object',
    'HTTP ' + docsBefore.status);

  const texts = docsBefore.body?.documentTexts || {};
  const terms = 'SMOKE conditions ' + stamp;
  // The admin's own change limit (default 3 a year, counted per kind) is enforced on the server,
  // and a pass of this script spends two of them: one to write the smoke terms and one to put
  // the company's own wording back. So it asks what is left before deciding which half of the
  // rule to prove - that the admin can rewrite, or that the limit refuses and leaves the
  // wording alone. Meeting the limit is the feature working, not a reason to fail.
  const invoiceAllowance = (docsBefore.body?.allowances || []).find((a) => a.kind === 'invoice');
  check('every printed kind reports its own allowance for the year',
    Array.isArray(docsBefore.body?.allowances) && docsBefore.body.allowances.length >= 8
    && !!invoiceAllowance && typeof invoiceAllowance.remaining === 'number',
    `invoice ${invoiceAllowance?.used}/${invoiceAllowance?.limit} used`);

  const canRewrite = (invoiceAllowance?.remaining ?? 0) >= 2;
  const written = await json('/api/catalog/store/documents', {
    method: 'PUT',
    body: JSON.stringify({
      documentTexts: { ...texts, invoice: { ...(texts.invoice || {}), fr: terms } },
      email: docsBefore.body?.email ?? null, website: docsBefore.body?.website ?? null,
      bankDetails: 'SMOKE BEA 000 ' + stamp,
    }),
  }, token);

  if (canRewrite) {
    check('the admin can write the invoice terms and the bank details',
      written.status === 200 && written.body?.documentTexts?.invoice?.fr === terms && written.body?.bankDetails === 'SMOKE BEA 000 ' + stamp,
      'HTTP ' + written.status + ' ' + JSON.stringify(written.body?.documentTexts?.invoice));

    const reread = await json('/api/catalog/store/documents', {}, token);
    check('what was written is what reads back', reread.body?.documentTexts?.invoice?.fr === terms);

    const spent = (reread.body?.allowances || []).find((a) => a.kind === 'invoice');
    check('a rewrite is counted against that kind and against no other',
      spent?.used === invoiceAllowance.used + 1
      && (reread.body?.allowances || []).filter((a) => a.kind !== 'invoice').every((a) =>
        a.used === docsBefore.body.allowances.find((o) => o.kind === a.kind)?.used),
      `invoice ${invoiceAllowance?.used} -> ${spent?.used}`);
  } else {
    check('a rewrite past the yearly limit the admin set himself is refused',
      written.status === 400, 'HTTP ' + written.status);
    check('the refusal is said in words rather than failing silently',
      typeof written.body === 'string' && written.body.length > 0,
      String(written.body).slice(0, 140));

    const untouched = await json('/api/catalog/store/documents', {}, token);
    check('a refused rewrite leaves the wording that was already there',
      untouched.body?.documentTexts?.invoice?.fr === texts.invoice?.fr);
  }

  const wrongKind = await json('/api/catalog/store/documents', {
    method: 'PUT', body: JSON.stringify({ documentTexts: { brochure: { fr: 'x' } } }),
  }, token);
  check('a text for a document the system does not print is refused', wrongKind.status === 400, 'HTTP ' + wrongKind.status);

  const storeAfter = await json('/api/catalog/store', {}, token);
  check('writing document texts never bumps the replicated store row',
    typeof storeBefore.body?.version === 'number' && storeAfter.body?.version === storeBefore.body?.version,
    `version ${storeBefore.body?.version} -> ${storeAfter.body?.version}`);

  // Leave the company's own texts as they were found.
  const restored = await json('/api/catalog/store/documents', {
    method: 'PUT', body: JSON.stringify(docsBefore.body),
  }, token);
  check('the original texts are put back', restored.status === 200, 'HTTP ' + restored.status);

  // ---------- named contract wordings (owner, PinSay #302, 2026-10-05) ----------
  // A wording is added beside the standard one with its own text and its own allowance, a draft
  // contract names it, the wording cannot be removed while the contract does, and once the draft
  // is back on the standard wording it can — its last text kept in its history. A fresh key each
  // run, and the company's list is put back as it was found.
  {
    const found = await json('/api/catalog/store/documents', {}, token);
    const key = 'rentalContract.s' + stamp.toLowerCase().replace(/[^a-z0-9]/g, '').slice(-8);
    const listed = found.body?.contractWordings || [];
    const put = (wordings, texts) => json('/api/catalog/store/documents', {
      method: 'PUT',
      body: JSON.stringify({ ...found.body, allowances: undefined, contractWordings: wordings, documentTexts: texts }),
    }, token);

    const added = await put(
      [...listed, { key, names: { fr: 'Smoke ' + stamp, ar: 'تجربة' } }],
      { ...found.body?.documentTexts, [key]: { fr: 'SMOKE conditions échafaudage ' + stamp } });
    check('a named contract wording is kept with its name and its text',
      added.status === 200 && added.body?.contractWordings?.some((w) => w.key === key && w.names?.ar === 'تجربة')
      && added.body?.documentTexts?.[key]?.fr === 'SMOKE conditions échafaudage ' + stamp,
      'HTTP ' + added.status + ' ' + JSON.stringify(added.body).slice(0, 160));
    check('the wording is counted on its own, its first change spent',
      added.body?.allowances?.find((a) => a.kind === key)?.used === 1,
      JSON.stringify(added.body?.allowances?.find((a) => a.kind === key)));

    if (tracked && depot && renter.status === 201 && renterSite.status === 201) {
      const draft = {
        customerId: renter.body.id, branchId: depot.id, siteId: renterSite.body.id, notes: 'SMOKE wording',
        lines: [{ productId: tracked.id, qty: 1, durationDays: 30, rentUnitPrice: 100, damageUnitPrice: 2000 }],
      };
      const named = await json('/api/rentals/contracts', { method: 'POST', body: JSON.stringify({ ...draft, wordingKey: key }) }, token);
      check('a contract names the wording it prints', named.status === 201 && named.body?.wordingKey === key,
        'HTTP ' + named.status + ' ' + named.body?.wordingKey);

      const inUse = await put(listed, found.body?.documentTexts);
      check('a wording a contract prints cannot be removed, and the refusal says so',
        inUse.status === 400 && /cannot be removed/.test(String(inUse.body)), 'HTTP ' + inUse.status + ' ' + String(inUse.body).slice(0, 140));

      const standard = named.status === 201
        ? await json('/api/rentals/contracts/' + named.body.id, { method: 'PUT', body: JSON.stringify({ ...draft, wordingKey: '' }) }, token)
        : { status: 0 };
      check('a draft goes back to the standard wording', standard.status === 200 && !standard.body?.wordingKey,
        'HTTP ' + standard.status);
    }

    const removed = await put(listed, found.body?.documentTexts);
    check('a wording no contract prints is removed, and the list is as it was found',
      removed.status === 200 && !(removed.body?.contractWordings || []).some((w) => w.key === key)
      && (removed.body?.contractWordings || []).length === listed.length,
      'HTTP ' + removed.status + ' ' + String(removed.body).slice(0, 140));
    const kept = await json('/api/catalog/store/documents/history?kind=' + encodeURIComponent(key), {}, token);
    check('the removed wording\'s text is kept in its history',
      kept.status === 200 && (kept.body || []).some((h) => h.previousTexts?.fr === 'SMOKE conditions échafaudage ' + stamp),
      'HTTP ' + kept.status + ' rows ' + (kept.body || []).length);
  }

  // ---------- an employee's name in two parts (owner, PinSay #301, 2026-10-05) ----------
  // The full name is composed first name first, whatever the client composed. One person, found
  // again on a re-run and edited rather than hired twice.
  if (depot) {
    const person = {
      fullName: 'ignored by the server', firstName: 'Smoke', familyName: 'Deux Parties', branchId: depot.id,
      payBasis: 'Monthly', baseRate: 30000, payoutMethod: 'Cash', hiredOn: '2020-01-01', isActive: true,
      attendanceModeOverride: 'Declared', jobTitle: 'SMOKE', notes: 'provider-smoke.cjs', email: '',
    };
    const already = await json(`/api/hr/employees?branchId=${depot.id}&q=${encodeURIComponent('Smoke Deux Parties')}`, {}, token);
    const existing = (already.body?.rows || []).find((e) => e.fullName === 'Smoke Deux Parties');
    const saved = existing
      ? await json('/api/hr/employees/' + existing.id, { method: 'PUT', body: JSON.stringify(person) }, token)
      : await json('/api/hr/employees', { method: 'POST', body: JSON.stringify(person) }, token);
    check('an employee is filed in two name parts, printed first name first',
      (saved.status === 200 || saved.status === 201) && saved.body?.fullName === 'Smoke Deux Parties'
      && saved.body?.firstName === 'Smoke' && saved.body?.familyName === 'Deux Parties',
      'HTTP ' + saved.status + ' ' + saved.body?.fullName);
    const half = await json('/api/hr/employees', { method: 'POST', body: JSON.stringify({ ...person, familyName: '  ' }) }, token);
    check('a first name without a family name is refused', half.status === 400 && half.body?.code === 'nameRequired',
      'HTTP ' + half.status + ' ' + JSON.stringify(half.body));
  }

  // ---------- the logo on every print (answer 30): an image, kept as a file, read signed-in only ----------
  // After the texts are back, so these saves change no wording and spend none of the allowance. The
  // server keeps the file in its own folder and answers a link: sending that link back keeps the
  // logo, a data URL replaces it, null removes it.
  const pngBase64 = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==';
  const logo = 'data:image/png;base64,' + pngBase64;
  // Read before it is replaced: once it is, its old link no longer names the current logo.
  const ownLogo = await logoAsDataUrl(docsBefore.body?.logoUrl, token);
  const logoSet = await json('/api/catalog/store/documents', {
    method: 'PUT', body: JSON.stringify({ ...docsBefore.body, logoUrl: logo }),
  }, token);
  const logoRead = await json('/api/catalog/store/documents', {}, token);
  const logoLink = logoRead.body?.logoUrl;
  check('the documents logo is written and reads back as a link, never as the image',
    logoSet.status === 200 && typeof logoLink === 'string' && logoLink.startsWith('/api/catalog/store/logo')
      && !JSON.stringify(logoRead.body).includes(pngBase64),
    'HTTP ' + logoSet.status + ' ' + String(logoLink).slice(0, 60));
  const logoFile = await bytes(logoLink, token);
  check('the link serves the same image to a signed-in user',
    logoFile.status === 200 && logoFile.type === 'image/png' && logoFile.buffer.equals(Buffer.from(pngBase64, 'base64')),
    'HTTP ' + logoFile.status + ' ' + logoFile.type + ' ' + logoFile.buffer.length + ' bytes');
  const logoAnonymous = await bytes(logoLink);
  check('the link serves nothing without a sign-in', logoAnonymous.status === 401 || logoAnonymous.status === 403,
    'HTTP ' + logoAnonymous.status);
  const logoKept = await json('/api/catalog/store/documents', {
    method: 'PUT', body: JSON.stringify({ ...docsBefore.body, logoUrl: logoLink }),
  }, token);
  const logoKeptRead = await json('/api/catalog/store/documents', {}, token);
  check('sending the link back keeps the logo', logoKept.status === 200 && logoKeptRead.body?.logoUrl === logoLink,
    'HTTP ' + logoKept.status + ' ' + String(logoKeptRead.body?.logoUrl).slice(0, 60));
  const logoHtml = await json('/api/catalog/store/documents', {
    method: 'PUT',
    body: JSON.stringify({ ...docsBefore.body, logoUrl: 'data:text/html;base64,' + Buffer.from('<script>alert(1)</script>').toString('base64') }),
  }, token);
  check('a logo that is not an image is refused', logoHtml.status === 400, 'HTTP ' + logoHtml.status);
  const logoForeign = await json('/api/catalog/store/documents', {
    method: 'PUT', body: JSON.stringify({ ...docsBefore.body, logoUrl: 'https://example.com/logo.png' }),
  }, token);
  check('a link that is not the current logo is refused', logoForeign.status === 400, 'HTTP ' + logoForeign.status);
  const logoStill = await json('/api/catalog/store/documents', {}, token);
  check('a refused logo leaves the one already there', logoStill.body?.logoUrl === logoLink);
  const logoBack = await json('/api/catalog/store/documents', {
    method: 'PUT', body: JSON.stringify({ ...docsBefore.body, logoUrl: ownLogo }),
  }, token);
  const logoAfter = await json('/api/catalog/store/documents', {}, token);
  check("the company's own logo is put back",
    logoBack.status === 200 && (ownLogo ? typeof logoAfter.body?.logoUrl === 'string' : (logoAfter.body?.logoUrl ?? null) === null),
    'HTTP ' + logoBack.status);

  // ---------- a branch's own fiscal identifiers (answer 29) round-trip, and are put back ----------
  const branchList = await json('/api/catalog/branches', {}, token);
  const idBranch = (branchList.body || []).find((b) => !b.isMain && b.isActive) || (branchList.body || [])[0];
  if (idBranch) {
    // The logo's link is sent back as it is, which keeps it: a save without it would clear it.
    const branchBody = (fiscalIds, logoUrl = idBranch.logoUrl ?? null, extra = {}) => JSON.stringify({
      nameAr: idBranch.nameAr, nameFr: idBranch.nameFr, nameEn: idBranch.nameEn,
      address: idBranch.addressFr || idBranch.address || '', phone: idBranch.phone,
      isMain: idBranch.isMain, allowNegativeStock: idBranch.allowNegativeStock, isActive: idBranch.isActive,
      addressAr: idBranch.addressAr ?? null, addressEn: idBranch.addressEn ?? null, fiscalIds, logoUrl, ...extra,
    });
    const ids = { rc: '16/00-SMK' + stamp, nif: '0016' + stamp, nis: '0999' + stamp, ai: '16SMK' + stamp };
    // Another code is sent with this save, as an older client might: the branch's code is the server's
    // and never changes, because every customer and employee number drawn there carries it.
    const otherCode = idBranch.code === '999' ? '998' : '999';
    const idsSet = await json('/api/catalog/branches/' + idBranch.id, {
      method: 'PUT', body: branchBody(ids, undefined, { code: otherCode }),
    }, token);
    const idsRead = (await json('/api/catalog/branches', {}, token)).body?.find((b) => b.id === idBranch.id);
    check("a branch's fiscal identifiers are written and read back",
      idsSet.status === 200 && ['rc', 'nif', 'nis', 'ai'].every((k) => idsRead?.fiscalIds?.[k] === ids[k]),
      'HTTP ' + idsSet.status + ' ' + JSON.stringify(idsRead?.fiscalIds));
    check("a branch's code is read-only: a save that sends another keeps it",
      BRANCH_CODE.test(idBranch.code ?? '') && idsRead?.code === idBranch.code,
      idBranch.code + ' → ' + idsRead?.code);
    const idsBad = await json('/api/catalog/branches/' + idBranch.id, { method: 'PUT', body: branchBody({ ssn: 'x' }) }, token);
    check('an identifier the country profile does not define is refused', idsBad.status === 400, 'HTTP ' + idsBad.status);
    const idsBack = await json('/api/catalog/branches/' + idBranch.id, { method: 'PUT', body: branchBody(idBranch.fiscalIds || {}) }, token);
    check("the branch's identifiers are put back", idsBack.status === 200, 'HTTP ' + idsBack.status);

    // ---------- a branch's logo: a file behind a signed-in link, like the company's ----------
    const ownBranchLogo = await logoAsDataUrl(idBranch.logoUrl, token);
    const branchLogoSet = await json('/api/catalog/branches/' + idBranch.id, {
      method: 'PUT', body: branchBody(idBranch.fiscalIds || {}, logo),
    }, token);
    const branchLogoLink = (await json('/api/catalog/branches', {}, token)).body?.find((b) => b.id === idBranch.id)?.logoUrl;
    check("a branch's logo is written and reads back as its own link",
      branchLogoSet.status === 200 && typeof branchLogoLink === 'string'
        && branchLogoLink.startsWith('/api/catalog/branches/' + idBranch.id + '/logo'),
      'HTTP ' + branchLogoSet.status + ' ' + String(branchLogoLink).slice(0, 80));
    const branchLogoFile = await bytes(branchLogoLink, token);
    const branchLogoAnonymous = await bytes(branchLogoLink);
    check("the branch's logo is served signed-in only",
      branchLogoFile.status === 200 && branchLogoFile.buffer.equals(Buffer.from(pngBase64, 'base64'))
        && (branchLogoAnonymous.status === 401 || branchLogoAnonymous.status === 403),
      'HTTP ' + branchLogoFile.status + ' / anonymous HTTP ' + branchLogoAnonymous.status);
    const branchLogoBack = await json('/api/catalog/branches/' + idBranch.id, {
      method: 'PUT', body: branchBody(idBranch.fiscalIds || {}, ownBranchLogo),
    }, token);
    check("the branch's own logo is put back", branchLogoBack.status === 200, 'HTTP ' + branchLogoBack.status);
  }

  // ---------- a receipt number used twice: kept under a suffixed number ----------
  // The same id twice is a harmless re-push. The same receipt number under another id is a second
  // computer on the same till; since 2026-10-05 (each cashier's own till) the sale is never refused
  // for it: it is recorded under the number with a /R suffix, and the push says so in words. The
  // unique index on (DeviceCode, Reference) stays, and this proves the renumbered row gets past it
  // on this provider.
  const catalogue = await json('/api/catalog/products', {}, token);
  const sold = (Array.isArray(catalogue.body) ? catalogue.body : catalogue.body?.items || [])
    .find((p) => !p.isDeleted && Number(p.price) > 0);
  if (!sold) {
    console.log('SKIP  the duplicate-receipt check needs a priced product; this database has none');
  } else {
    const receipt = 'SMOKE-DUP-' + Date.now().toString(36).toUpperCase();
    const price = Number(sold.price);
    // A sequence no earlier run used: the till's local counter is unique per terminal as well.
    let localSeq = Date.now();
    const sale = () => ({
      id: crypto.randomUUID(), reference: receipt, localSeq: localSeq++, soldAtUtc: new Date().toISOString(), customerId: null,
      cashierUserId: login.body.user.id, cashierName: login.body.user.fullName,
      subTotalHt: price, taxTotal: 0, discountTotal: 0, grandTotalTtc: price, paidAmount: price, changeAmount: 0,
      paymentMethod: 'Cash', status: 'Completed', refundOfSaleId: null, note: 'provider smoke', taxMode: 'untaxed',
      lines: [{ id: crypto.randomUUID(), productId: sold.id, sku: sold.sku, nameSnapshot: sold.nameFr || sold.sku,
        qty: 1, unitPrice: price, taxRate: 0, lineDiscount: 0, lineTotalTtc: price, lineTaxAmount: 0 }],
      payments: [{ id: crypto.randomUUID(), method: 'cash', amount: price, reference: null, methodKey: 'cash' }],
    });
    const push = (s) => json('/api/sync/push', { method: 'POST', body: JSON.stringify({ deviceCode: 'C01', sales: [s] }) }, token);
    const first = await push(sale());
    check('a sale pushed from the till is recorded', first.status === 200 && first.body?.accepted === 1,
      'HTTP ' + first.status + ' ' + JSON.stringify(first.body?.results?.[0] ?? first.body).slice(0, 160));
    const again = await push(sale());
    const result = again.body?.results?.[0];
    check(`the same receipt number under another id is kept under a suffixed number on ${PROVIDER}`,
      again.status === 200 && again.body?.accepted === 1 && result?.status === 'accepted'
        && String(result?.message ?? '').includes(`${receipt}/R`),
      'HTTP ' + again.status + ' ' + JSON.stringify(result ?? again.body).slice(0, 160));
  }

  console.log(failures === 0 ? `\nALL CHECKS PASSED (${PROVIDER})` : `\n${failures} CHECK(S) FAILED`);
  process.exit(failures === 0 ? 0 : 1);
})();
