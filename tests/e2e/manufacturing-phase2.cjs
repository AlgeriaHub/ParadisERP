// Manufacturing Phase 2, end to end against a running API on a disposable database with
// Manufacturing__StockOperations=true: an order's whole life through the API — reservation, issue,
// the daily record with hours against attendance, approval by another manager, partial releases,
// the leftover count, completion — a till sale of a material for the POS report, then the screens in
// French and Arabic at 1440 px and Arabic at 390 px. Screenshots go to artifacts/manufacturing/phase2.
//
//   ADMIN_PASSWORD=... BASE=http://127.0.0.1:5198 node tests/e2e/manufacturing-phase2.cjs
const { requiredPassword } = require('./test-credentials.cjs');
const password = requiredPassword();
const { chromium } = require('playwright');
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict'), http = require('node:http');
const crypto = require('node:crypto');

const api = process.env.BASE || 'http://127.0.0.1:5198';
const root = path.resolve(__dirname, '../..');
const out = path.join(root, 'artifacts/manufacturing/phase2');
fs.mkdirSync(out, { recursive: true });
const WEB_PORT = 5192;
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
const as = (token) => ({
  req: async (p, body, method) => {
    const r = await call(token, p, body, method);
    if (r.status >= 300) throw Error(`${method ?? (body ? 'POST' : 'GET')} ${p}: ${r.status} ${JSON.stringify(r.data)}`);
    return r.data;
  },
  refused: async (status, code, p, body, method) => {
    const r = await call(token, p, body, method);
    eq([r.status, r.data?.code], [status, code], `${p} should be refused ${status} ${code} (${JSON.stringify(r.data).slice(0, 160)})`);
  },
});
const rid = () => crypto.randomUUID();
const day = (offset = 0) => {
  const now = new Date(Date.now() + offset * 86_400_000);
  const parts = new Intl.DateTimeFormat('en-CA', { timeZone: 'Africa/Algiers', year: 'numeric', month: '2-digit', day: '2-digit' }).formatToParts(now);
  const get = (t) => parts.find((p) => p.type === t).value;
  return `${get('year')}-${get('month')}-${get('day')}`;
};

async function seed() {
  const signIn = (await call(null, '/api/auth/login', { username: 'admin', password, deviceCode: 'C01' })).data;
  const adminToken = signIn.token;
  const a = as(adminToken);
  eq((await a.req('/api/manufacturing/operations/status')).stockOperationsEnabled, true, 'stock operations are on for this disposable database');
  const units = Object.fromEntries((await a.req('/api/manufacturing/units')).map((u) => [u.code, u.id]));
  const stamp = Date.now().toString().slice(-5);

  const warehouse = await a.req('/api/catalog/branches', {
    nameAr: 'ورشة المسامير', nameFr: 'Atelier clouterie', nameEn: 'Nail shop', address: '', phone: '', isMain: false,
    allowNegativeStock: false, isActive: true, isWarehouse: true,
  });
  const warehouse2 = await a.req('/api/catalog/branches', {
    nameAr: 'ورشة المسامير ب', nameFr: 'Atelier clouterie B', nameEn: 'Nail shop B', address: '', phone: '', isMain: false,
    allowNegativeStock: false, isActive: true, isWarehouse: true,
  });
  const product = (sku, fr, ar, price, costPrice = 0) => a.req('/api/catalog/products', {
    sku: `${sku}-${stamp}`, barcode: null, nameAr: ar, nameFr: fr, nameEn: fr, categoryId: null, price, costPrice, taxRate: 0,
    unit: 'pcs', trackStock: true, isActive: true, isRentable: false, assetUnitValue: 0,
  });
  // The wire's reference cost (150 a kg) is what an issue freezes, and what the costing preview values it at.
  const wire = await product('WIRE', 'Fil de fer 3 mm', 'سلك حديد 3 مم', 0, 150);
  const nails = await product('NAIL', 'Pointes 70 mm', 'مسامير 70 مم', 500);
  await a.req(`/api/manufacturing/materials/${nails.id}`, { role: 'Finished', baseUnitId: units.kg, units: [] }, 'PUT');
  const line = (await a.req('/api/manufacturing/lines', {
    code: `LA${stamp}`, nameAr: 'خط المسامير', nameFr: 'Ligne clouterie', nameEn: 'Nail line', description: null, locationId: warehouse.id,
    managerUserId: null, isActive: true,
  })).line;
  const line2 = (await a.req('/api/manufacturing/lines', {
    code: `LB${stamp}`, nameAr: 'خط المسامير ب', nameFr: 'Ligne clouterie B', nameEn: 'Nail line B', description: null, locationId: warehouse2.id,
    managerUserId: null, isActive: true,
  })).line;
  await a.req(`/api/manufacturing/lines/${line.id}/products`, { productIds: [nails.id] }, 'PUT');
  await a.req(`/api/manufacturing/lines/${line2.id}/products`, { productIds: [nails.id] }, 'PUT');
  await a.req(`/api/catalog/products/${wire.id}/stock`, { branchId: warehouse.id, qtyDelta: 300, reason: 'Synthetic opening', reorderLevel: null });
  await a.req(`/api/catalog/products/${wire.id}/stock`, { branchId: warehouse2.id, qtyDelta: 300, reason: 'Synthetic opening', reorderLevel: null });
  // Classified after its stock is in: a till sale of it made before this moment is stale offline data.
  const tillToken = adminToken;
  const main = (await a.req('/api/catalog/branches')).find((b) => b.isMain && !b.isWarehouse);
  await a.req(`/api/catalog/products/${wire.id}/stock`, { branchId: main.id, qtyDelta: 20, reason: 'Synthetic opening', reorderLevel: null });
  await a.req(`/api/manufacturing/materials/${wire.id}`, { role: 'RawMaterial', baseUnitId: units.kg, units: [] }, 'PUT');
  const sale = (soldAt, ref) => ({
    id: rid(), reference: ref, localSeq: Number(String(Date.now()).slice(-7)) + Math.floor(Math.random() * 1000), soldAtUtc: soldAt,
    customerId: null, cashierUserId: signIn.user.id, cashierName: signIn.user.fullName, subTotalHt: 1000, taxTotal: 0, discountTotal: 0, grandTotalTtc: 1000,
    roundingAdjustment: 0, paidAmount: 1000, changeAmount: 0, paymentMethod: 'Cash', status: 'Completed', refundOfSaleId: null, note: null,
    taxMode: 'untaxed',
    lines: [{ id: rid(), productId: wire.id, sku: wire.sku, nameSnapshot: wire.nameFr, qty: 2, unitPrice: 500, taxRate: 0, lineDiscount: 0, lineTotalTtc: 1000, lineTaxAmount: 0 }],
    payments: [{ id: rid(), method: 'cash', amount: 1000, reference: null, methodKey: 'cash' }],
  });
  const pushed = await call(tillToken, '/api/sync/push', { deviceCode: 'C01', sales: [sale(new Date(Date.now() - 2 * 86_400_000).toISOString(), `M2-OLD-${stamp}`), sale(new Date().toISOString(), `M2-NEW-${stamp}`)] });
  eq([pushed.status, pushed.data.accepted], [200, 2], 'two till sales of the material are recorded, never refused');
  ok(pushed.data.results.every((r) => (r.message || '').includes('not for sale')), 'the till is told each was a material not for sale');

  await a.req('/api/manufacturing/boms', {
    productId: nails.id, productionLineId: line.id, batchQuantity: 100, batchUnitId: units.kg, stageTemplateId: null, notes: null,
    lines: [{ componentProductId: wire.id, quantity: 100, unitId: units.kg, wastePercent: 5, specification: 'Ø 3 mm, S235', note: null }],
    stages: [
      { nameAr: 'القص', nameFr: 'Coupe', nameEn: 'Cut', estimatedMinutes: 60, expectedWorkers: 2, isQualityCheckpoint: false, qualityNote: null, isOutput: false, roles: [] },
      { nameAr: 'التشكيل', nameFr: 'Frappe', nameEn: 'Form', estimatedMinutes: 30, expectedWorkers: 1, isQualityCheckpoint: true, qualityNote: 'Tête et longueur', isOutput: true, roles: [] },
    ],
  }).then((b) => a.req(`/api/manufacturing/boms/${b.bom.id}/activate`, {}));
  await a.req('/api/manufacturing/boms', {
    productId: nails.id, productionLineId: line2.id, batchQuantity: 100, batchUnitId: units.kg, stageTemplateId: null, notes: null,
    lines: [{ componentProductId: wire.id, quantity: 100, unitId: units.kg, wastePercent: 5, specification: 'Ø 3 mm, S235', note: null }],
    stages: [{ nameAr: 'القص', nameFr: 'Coupe', nameEn: 'Cut', estimatedMinutes: 60, expectedWorkers: 1, isQualityCheckpoint: false, qualityNote: null, isOutput: true, roles: [] }],
  }).then((b) => a.req(`/api/manufacturing/boms/${b.bom.id}/activate`, {}));

  // A worker whose attendance is recorded day by day: 8 h today.
  const employee = (name, mode) => a.req('/api/hr/employees', {
    fullName: name, branchId: main.id, payBasis: 'Monthly', baseRate: 52000, payoutMethod: 'Cash', hiredOn: '2025-01-01',
    attendanceModeOverride: mode, firstName: name.split(' ')[0], familyName: name.split(' ')[1],
  });
  const karim = await employee('Karim Benali', 'Recorded');
  const concurrent = await employee('Nabil Concurrent', 'Recorded');
  await a.req(`/api/manufacturing/lines/${line.id}/employees`, { employeeId: karim.id, jobRoleId: null, assignedOn: '2026-01-01', note: null });
  await a.req(`/api/manufacturing/lines/${line.id}/employees`, { employeeId: concurrent.id, jobRoleId: null, assignedOn: '2026-01-01', note: null });
  await a.req(`/api/manufacturing/lines/${line2.id}/employees`, { employeeId: concurrent.id, jobRoleId: null, assignedOn: '2026-01-01', note: null });
  await a.req('/api/hr/attendance/days', { days: [{ employeeId: karim.id, workDate: day(), status: 'Present', hoursWorked: 8 }] }, 'PUT');
  await a.req('/api/hr/attendance/days', { days: [{ employeeId: concurrent.id, workDate: day(), status: 'Present', hoursWorked: 8 }] }, 'PUT');
  // A second administrator, so the daily record is approved by someone who did not submit it.
  const nadia = await employee('Nadia Mansouri', 'Recorded');
  const account = await a.req('/api/users', { username: null, fullName: null, role: 'Admin', password: `${password}-2`, pin: null, branchId: null, employeeId: nadia.id });
  const managerToken = (await call(null, '/api/auth/login', { username: account.username, password: `${password}-2`, deviceCode: null })).data.token;
  return { a, adminToken, m: as(managerToken), line, line2, wire, nails, karim, concurrent, units, warehouse, main, stamp, account };
}

async function lifecycle(w) {
  const { a, adminToken, m, line, line2, wire, nails, karim, concurrent, units, main } = w;
  const stockOf = async (productId, branchId) => (await a.req(`/api/manufacturing/lines/${line.id}/stock`)).find((r) => r.productId === productId)?.available ?? 0;

  // ---- order: availability, reservation, a retried request ----
  const body = { lineId: line.id, productId: nails.id, plannedQty: 200, plannedUnitId: units.kg, plannedStart: day(-1), plannedEnd: day(-1),
    notes: 'Commande chantier', clientRequestId: rid(), startShort: false, shortageReason: null };
  const preview = await a.req('/api/manufacturing/orders/preview', body);
  eq([preview.batches, preview.materials[0].requiredBaseQty, preview.materials[0].freeBaseQty, preview.short], [2, 210, 300, false], '200 kg of nails need 210 kg of wire, all free');
  const order = await a.req('/api/manufacturing/orders', body);
  const again = await a.req('/api/manufacturing/orders', body);
  eq(again.order.id, order.order.id, 'the same request makes the same order');
  await a.refused(409, 'material_shortage', '/api/manufacturing/orders', { ...body, clientRequestId: rid() });
  const id = order.order.id;
  const material = order.materials[0].id;

  // ---- issue, under one request id twice ----
  const issue = { kind: 'Issue', lines: [{ orderMaterialId: material, qty: 210, unitId: units.kg }], note: null, clientRequestId: rid() };
  await a.req(`/api/manufacturing/orders/${id}/movements`, issue);
  await a.req(`/api/manufacturing/orders/${id}/movements`, issue);
  eq(await stockOf(wire.id), 90, 'the issue left the line once: 300 − 210');

  // ---- the daily record: rows, hours against attendance, approval by another manager ----
  const log = await a.req('/api/manufacturing/logs', { lineId: line.id, day: day() });
  const [cut, form] = order.stages.map((s) => s.id);
  const saved = await a.req(`/api/manufacturing/logs/${log.log.id}`, { notes: 'Presse n°2', rows: [
    { orderId: id, stageId: cut, category: null, specification: null, plannedQty: 200, completedQty: 200, rejectedQty: 0, rejectReason: null, notes: null },
    { orderId: id, stageId: form, category: null, specification: null, plannedQty: 200, completedQty: 196, rejectedQty: 4, rejectReason: 'Têtes déformées', notes: null },
  ] }, 'PUT');
  const [rowCut, rowForm] = saved.rows.map((r) => r.id);
  // Separate lines make independently-scoped HTTP requests. The shared version lock permits only
  // one booking where 5 h + 4 h would exceed Nabil's 8 h attendance capacity.
  const parallelLabour = await a.req('/api/manufacturing/orders', { ...body, lineId: line2.id, plannedQty: 50, clientRequestId: rid() });
  const parallelLog = await a.req('/api/manufacturing/logs', { lineId: line2.id, day: day() });
  const parallelSaved = await a.req(`/api/manufacturing/logs/${parallelLog.log.id}`, { notes: null, rows: [{
    orderId: parallelLabour.order.id, stageId: parallelLabour.stages[0].id, category: null, specification: null,
    plannedQty: 50, completedQty: 10, rejectedQty: 0, rejectReason: null, notes: null,
  }] }, 'PUT');
  const booking = (logId, rowId, hours) => call(adminToken, `/api/manufacturing/logs/${logId}/rows/${rowId}/bookings`,
    { employeeId: concurrent.id, hours, overrideReason: null });
  const labourRace = await Promise.all([booking(log.log.id, rowCut, 5), booking(parallelLog.log.id, parallelSaved.rows[0].id, 4)]);
  eq(labourRace.map((r) => r.status).sort(), [200, 409], 'concurrent labour bookings cannot exceed attendance capacity');
  eq(labourRace.find((r) => r.status === 409).data.code, 'exceeds_attendance', 'the losing concurrent booking says why');
  // Either booking may win. When the 5 h on this order wins, the order carries Nabil's 5 h beside Karim's 8.
  const labourWonHere = labourRace[0].status === 200;
  const orderHours = 8 + (labourWonHere ? 5 : 0);
  await a.req(`/api/manufacturing/logs/${log.log.id}/rows/${rowCut}/bookings`, { employeeId: karim.id, hours: 6, overrideReason: null });
  await a.refused(409, 'exceeds_attendance', `/api/manufacturing/logs/${log.log.id}/rows/${rowForm}/bookings`, { employeeId: karim.id, hours: 3, overrideReason: null });
  await a.req(`/api/manufacturing/logs/${log.log.id}/rows/${rowForm}/bookings`, { employeeId: karim.id, hours: 2, overrideReason: null });
  await a.req(`/api/manufacturing/logs/${log.log.id}/submit`, {});
  await a.refused(403, 'own_record', `/api/manufacturing/logs/${log.log.id}/approve`, {});
  const approved = await m.req(`/api/manufacturing/logs/${log.log.id}/approve`, {});
  eq(approved.log.status, 'Approved', 'approved by a manager who did not submit it');

  // ---- partial releases: the order stays open between them ----
  const afterLog = await a.req(`/api/manufacturing/orders/${id}`);
  eq([afterLog.producedBaseQty, afterLog.awaitingReleaseBaseQty], [196, 196], 'only the output stage is finished output: 196, not 200 + 196');
  const first = await m.req(`/api/manufacturing/orders/${id}/releases`, { qty: 100, unitId: units.kg, note: null, clientRequestId: rid() });
  eq([first.order.status, first.order.acceptedBaseQty, first.remainingBaseQty], ['Open', 100, 100], 'a partial release keeps the order open');
  await m.req(`/api/manufacturing/orders/${id}/releases`, { qty: 96, unitId: units.kg, note: null, clientRequestId: rid() });

  // ---- the leftover count, then completion approved by the other manager ----
  await a.refused(409, 'materials_at_line', `/api/manufacturing/orders/${id}/request-completion`, {});
  await a.req(`/api/manufacturing/orders/${id}/consumptions`, { orderMaterialId: material, baseQty: 200, reason: 'Used', logRowId: rowCut, note: null, clientRequestId: rid() });
  await a.req(`/api/manufacturing/orders/${id}/consumptions`, { orderMaterialId: material, baseQty: 6, reason: 'Offcut', logRowId: null, note: 'Chutes < 5 cm', clientRequestId: rid() });
  await a.req(`/api/manufacturing/orders/${id}/movements`, { kind: 'Return', lines: [{ orderMaterialId: material, qty: 4, unitId: units.kg }], note: 'Bobine entamée', clientRequestId: rid() });
  // Two independent orders race for 10,000 of one voucher. The write lock serializes the aggregate
  // check: exactly one 6,000 allocation may commit, on every supported provider.
  const parallel = await a.req('/api/manufacturing/orders', { ...body, plannedQty: 50, clientRequestId: rid() });
  const capVoucher = (await a.req('/api/payment-vouchers', { branchId: main.id, kind: 2, amount: 10000, methodKey: 'cash',
    payeeName: 'Concurrent allocation', expenseCategory: (await a.req('/api/payment-vouchers/categories')).find((c) => !c.contractRequired).key,
    purpose: 'Concurrent allocation check' })).voucher;
  const allocation = (orderId, requestId) => call(adminToken, `/api/manufacturing/orders/${orderId}/expenses`,
    { description: 'Concurrent allocation', amount: 6000, paymentVoucherId: capVoucher.id, clientRequestId: requestId });
  const raced = await Promise.all([allocation(id, rid()), allocation(parallel.order.id, rid())]);
  eq(raced.map((r) => r.status).sort(), [200, 409], 'concurrent orders cannot overallocate one payment voucher');
  eq(raced.find((r) => r.status === 409).data.code, 'voucher_amount_exceeded', 'the losing concurrent allocation says why');
  // Either allocation may win. When this order's wins, it carries 6,000 beside the 12,000 below.
  const voucherWonHere = raced[0].status === 200;
  const orderExpenses = 12000 + (voucherWonHere ? 6000 : 0);
  console.log(`races: the 5 h booking on this order ${labourWonHere ? 'won' : 'lost'}; this order's 6,000 allocation ${voucherWonHere ? 'won' : 'lost'}`);
  // Step A0 (F9): the expense names the voucher that paid it, and a voucher is never counted above its amount.
  const category = (await a.req('/api/payment-vouchers/categories')).find((c) => !c.contractRequired);
  // PaymentVoucherKind is a numeric .NET enum in the API JSON contract: Expense = 2.
  const voucher = (await a.req('/api/payment-vouchers', { branchId: main.id, kind: 2, amount: 12000, methodKey: 'cash',
    payeeName: 'Galvanoplastie Est', expenseCategory: category.key, purpose: 'Galvanisation sous-traitée' })).voucher;
  await a.req(`/api/manufacturing/orders/${id}/expenses`, { description: 'Galvanisation sous-traitée', amount: 12000, paymentVoucherId: voucher.id, clientRequestId: rid() });
  await a.refused(409, 'voucher_amount_exceeded', `/api/manufacturing/orders/${id}/expenses`,
    { description: 'Transport', amount: 1, paymentVoucherId: voucher.id, clientRequestId: rid() });
  await a.req(`/api/manufacturing/orders/${id}/request-completion`, {});
  await a.refused(403, 'own_request', `/api/manufacturing/orders/${id}/approve-completion`, {});
  const done = await m.req(`/api/manufacturing/orders/${id}/approve-completion`, {});
  eq([done.order.status, done.order.acceptedBaseQty, done.order.rejectedBaseQty], ['Completed', 196, 4], 'completed with 196 kg accepted, 4 kg rejected');
  eq([await stockOf(wire.id), await stockOf(nails.id)], [94, 196], "the line's stock: wire 300 − 210 + 4, nails 196");

  const costs = await a.req(`/api/manufacturing/orders/${id}/cost-inputs`);
  eq([costs.labourHours, costs.materials[0].usedBaseQty], [orderHours, 206], `the costing inputs: ${orderHours} h and 206 kg used`);
  eq(costs.expensesTotal, orderExpenses, 'the winning concurrent allocation is recorded on the order that won it, and only there');
  ok(costs.labour[0].payrollMonth > 0 && !JSON.stringify(costs).includes('52000'), 'labour names its payroll month and shows no wage');
  const pending = await a.req('/api/manufacturing/reports/pending-movements');
  eq(pending.rows.map((r) => r.number).sort(), ['MI-000001', 'MR-000001', 'OR-000001', 'OR-000002'], 'every stock movement awaits posting');
  // ---- costing, Phase A (only when this server computes costs: Manufacturing__Costing=true) ----
  const status = await a.req('/api/manufacturing/operations/status');
  if (status.costingEnabled) {
    const sheet = await a.req(`/api/manufacturing/orders/${id}/costing`);
    eq([sheet.actual.materials, sheet.actual.expenses, sheet.completed], [30900, orderExpenses, true],
      `the cost sheet: 210 kg of wire issued and 4 returned at 150 (30,900), ${orderExpenses} of expenses, the order completed`);
    ok(sheet.actual.labour > 0 && sheet.labourHours === orderHours, `the order's ${orderHours} hours are costed at the payroll rate`);
    eq(Math.round((sheet.actual.materials + sheet.actual.labour + sheet.actual.overhead + sheet.actual.expenses - sheet.actual.recovery) * 1000),
      Math.round(sheet.actual.total * 1000), 'the elements add up to the total');
    eq([sheet.releases.length, sheet.releases[1].carriesRemainder, Math.round(sheet.releasedValue), Math.round(sheet.actual.total)],
      [2, true, Math.round(sheet.actual.total), Math.round(sheet.actual.total)], 'the last release carries the rest, so the releases carry the whole cost');
    ok(sheet.notes.some((n) => n.code === 'labour_standard_rate') && sheet.notes.some((n) => n.code === 'material_reference_cost'),
      'the sheet says the labour rate is standard (October not approved) and the wire is at its reference cost');
    const pendingValued = await a.req('/api/manufacturing/reports/pending-movements');
    eq([pendingValued.releasesValued, Math.round(pendingValued.releasedValue)], [true, Math.round(sheet.releasedValue)],
      'the awaiting-posting report values releases by the costing preview, never at the product catalogue cost');
    const wip = await a.req('/api/manufacturing/reports/work-in-progress');
    ok(!wip.rows.some((r) => r.orderId === id), 'a completed order is not in progress');
    const [year, month] = day().split('-').map(Number);
    const labour = await a.req(`/api/manufacturing/reports/labour-absorption?year=${year}&month=${month}`);
    eq(labour.rows.find((r) => r.employeeId === karim.id)?.orderHours, 8, "the labour report has Karim's 8 hours on orders");
    const flow = await a.req('/api/manufacturing/reports/material-flow');
    eq(flow.issues.filter((i) => i.kind !== 'stock_balance_mismatch').length, 0, 'every manufacturing document matches its stock movement');
  }
  const pos = await a.req('/api/manufacturing/reports/pos-exceptions');
  eq([pos.total, pos.staleCount, pos.afterWithdrawalCount], [2, 1, 1], 'the POS report shows the stale and the after-withdrawal sale');

  // ---- a second, open order and a draft record, for the screens ----
  const open = parallel;
  await a.req(`/api/manufacturing/orders/${open.order.id}/movements`, { kind: 'Issue', lines: [{ orderMaterialId: open.materials[0].id, qty: 30, unitId: units.kg }], note: null, clientRequestId: rid() });
  const draft = await a.req('/api/manufacturing/logs', { lineId: line.id, day: day(-1) });
  await a.req(`/api/manufacturing/logs/${draft.log.id}`, { notes: null, rows: [
    { orderId: open.order.id, stageId: open.stages[0].id, category: null, specification: null, plannedQty: 50, completedQty: 30, rejectedQty: 0, rejectReason: null, notes: null },
    { orderId: null, stageId: null, category: 'Breakdown', specification: null, plannedQty: 0, completedQty: 0, rejectedQty: 0, rejectReason: null, notes: 'Presse bloquée 40 min' },
  ] }, 'PUT');
  // Step A0 (F6): Karim's month is read from day rows (he has one today), so punches on a day without a row
  // count no hours, as payroll pays none for it.
  if (day(-1).slice(0, 7) === day().slice(0, 7)) {
    const [y, mo, d] = day(-1).split('-').map(Number);
    for (const [kind, hour] of [['In', 8], ['Out', 14]])
      await a.req('/api/hr/attendance/clock', { employeeId: karim.id, kind, atUtc: new Date(Date.UTC(y, mo - 1, d, hour)).toISOString() });
    const k = (await a.req(`/api/manufacturing/logs/${draft.log.id}/employee-candidates`)).find((p) => p.employeeId === karim.id);
    eq([k.attendanceHours, k.attendanceSource], [0, 'none'], 'a punched day without a row, in a month read from day rows, shows no hours');
  }
  return { completed: id, open: open.order.id, record: log.log.id, draft: draft.log.id, costing: status.costingEnabled };
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

async function shoot(browser, lang, width, ids) {
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

  const pages = [
    ['orders', 'manufacturing/orders', 'app-mfg-orders tbody tr'],
    ['order-completed', `manufacturing/orders/${ids.completed}`, 'app-mfg-order .facts'],
    ['order-open', `manufacturing/orders/${ids.open}`, 'app-mfg-order .facts'],
    ['records', 'manufacturing/records', 'app-mfg-records tbody tr'],
    ['record-approved', `manufacturing/records/${ids.record}`, 'app-mfg-record .row-card'],
    ['record-draft', `manufacturing/records/${ids.draft}`, 'app-mfg-record .row-card'],
    ['pos-exceptions', 'manufacturing/pos-exceptions', 'app-mfg-pos-exceptions tbody tr'],
    ['pending', 'manufacturing/awaiting-posting', 'app-mfg-pending-movements tbody tr'],
    ['access', 'manufacturing/access', 'app-mfg-access tbody tr'],
  ];
  if (ids.costing) {
    pages.push(
      ['work-in-progress', 'manufacturing/work-in-progress', 'app-mfg-work-in-progress tbody tr'],
      ['labour-cost', 'manufacturing/labour-cost', 'app-mfg-labour-cost tbody tr'],
      ['material-flow', 'manufacturing/material-flow', 'app-mfg-material-flow .ui-metrics'],
    );
    // The order pages wait for their cost sheet too.
    for (const page of pages) if (page[0].startsWith('order-')) page[2] = 'app-mfg-order-costing section';
  }
  const narrow = ['orders', 'order-open', 'record-draft', 'pos-exceptions', 'work-in-progress', 'labour-cost'];
  const wanted = width < 600 ? pages.filter(([name]) => narrow.includes(name)) : pages;
  for (const [name, address, ready] of wanted) {
    await page.goto(`${base}/#/backoffice/${address}`);
    await page.waitForSelector(ready, { timeout: 20000 });
    await page.waitForTimeout(400);
    eq(await page.evaluate(() => document.documentElement.dir), lang === 'ar' ? 'rtl' : 'ltr', `${lang} ${name}: reading direction`);
    ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `${lang} ${width} ${name}: nothing scrolls sideways`);
    eq(await page.evaluate(() => (document.body.innerText.match(/\b(mfg|perm)\.[a-zA-Z_.]+/g) || []).slice(0, 3)), [], `${lang} ${name}: no dictionary key shows untranslated`);
    await page.screenshot({ path: path.join(out, `${lang}-${width}-${name}.png`), fullPage: true });
  }
  if (width >= 600) {
    // The hours dialog on the draft record, where the day's attendance reads.
    await page.goto(`${base}/#/backoffice/manufacturing/records/${ids.draft}`);
    await page.waitForSelector('app-mfg-record .row-card');
    await page.locator('app-mfg-record .row-people button.btn-ghost').first().click();
    await page.waitForSelector('app-dialog #bk-emp');
    await page.waitForTimeout(600);
    await page.screenshot({ path: path.join(out, `${lang}-${width}-hours-dialog.png`) });
    await page.keyboard.press('Escape');
    // The access dialog with the production permissions.
    await page.goto(`${base}/#/backoffice/manufacturing/access`);
    await page.waitForSelector('app-mfg-access tbody tr');
    await page.locator('app-mfg-access tbody tr .ui-link').first().click();
    await page.waitForSelector('app-dialog .bo-check');
    await page.waitForTimeout(600);
    await page.screenshot({ path: path.join(out, `${lang}-${width}-access-dialog.png`) });
  }
  eq(errors, [], `${lang} ${width}: no page error`);
  await context.close();
}

async function main() {
  const world = await seed();
  const ids = await lifecycle(world);
  console.log(`PASS API: ${checks} assertions`);
  const server = await serve();
  const browser = await chromium.launch({ headless: true, channel: 'chrome' });
  try {
    await shoot(browser, 'fr', 1440, ids);
    await shoot(browser, 'ar', 1440, ids);
    await shoot(browser, 'ar', 390, ids);
    console.log(`PASS ${checks} Phase 2 assertions; screenshots in ${path.relative(root, out)}`);
  } finally {
    await browser.close();
    await new Promise((resolve) => server.close(resolve));
  }
}

main().catch((e) => { console.error(e); process.exitCode = 1; });
