const { requiredPassword } = require('./test-credentials.cjs');
// Synthetic local API check. Never point this at a customer database.
const assert = require('node:assert/strict');
const base = process.env.BASE || 'http://127.0.0.1:5187';
if (!/^http:\/\/(127\.0\.0\.1|localhost):/.test(base)) throw Error('Local test API required');
let token, checks = 0;
async function request(path, body, expected = 200, method = body === undefined ? 'GET' : 'POST') {
  const response = await fetch(base + path, {method, headers: {'content-type':'application/json', ...(token ? {authorization:`Bearer ${token}`} : {})}, body:body === undefined ? undefined : JSON.stringify(body)});
  const text = await response.text(); let data; try { data=JSON.parse(text); } catch { data=text; }
  assert.equal(response.status, expected, `${method} ${path}: ${JSON.stringify(data)}`); checks++; return data;
}
async function main() {
  token=(await request('/api/auth/login',{username:process.env.ADMIN_USER||'admin',password:requiredPassword('ADMIN_PASSWORD')})).token;
  const branches=await request('/api/catalog/branches'), branch=branches.find(b=>b.isMain);
  const products=await request('/api/catalog/products'), product=products.find(p=>p.trackStock);
  const settings=await request('/api/rentals/settings');
  await request('/api/rentals/settings',{...settings,minimumDays:7,taxRate:19},200,'PUT');
  try {
    for (const [usage,paid] of [['NotUsed',false],['NotUsed',true],['Used',false],['Used',true]]) {
      const customer=await request('/api/catalog/customers',{fullName:`Same day ${usage} ${paid} ${Date.now()}`,kind:'Person',creditLimit:1000000,phone:'0555 00 00 00',birthDate:'1985-04-12T00:00:00Z',idDocumentNumber:'109 876 543',idDocumentType:'nationalCard',idDocumentIssuer:'Synthetic test',idDocumentIssuedOn:'2019-06-01'},201);
      const site=await request(`/api/customers/${customer.id}/sites`,{name:'Synthetic site',address:'Test'},201);
      const contract=await request('/api/rentals/contracts',{customerId:customer.id,branchId:branch.id,siteId:site.id,lines:[{productId:product.id,qty:2,durationDays:30,rentUnitPrice:100,damageUnitPrice:2000}]},201);
      const line=contract.lines[0], prefix=`/api/rentals/contracts/${contract.id}`;
      const issue=await request(prefix+'/issue-notes',{branchId:branch.id,customerTransport:true,issuedAtUtc:'2026-10-14T08:00:00Z',lines:[{contractLineId:line.id,qty:2}]},201);
      const invoiced=await request('/api/receivables/invoices',{issueNoteId:issue.id,dueDateUtc:'2026-11-14T08:00:00Z'},201);
      const invoice=invoiced.invoice;
      assert.equal(invoice.grandTotalTtc,7140); checks++;
      if(paid) await request('/api/receivables/receipts',{customerId:customer.id,branchId:branch.id,amount:7140,methodKey:'cash',contractId:contract.id,allocations:[{invoiceId:invoice.id,amount:7140}]},201);
      const draft={branchId:branch.id,customerTransport:true,returnedAtUtc:'2026-10-14T14:00:00Z',lines:[{contractLineId:line.id,qtyOk:2}]};
      const undecided=await request(prefix+'/return-notes/preview',draft);
      assert.equal(undecided.canConfirm,false); assert.equal(undecided.rows[0].sameDayUsage,null); checks+=2;
      await request(prefix+'/return-notes',draft,400);
      draft.lines[0].sameDayUsage=usage;
      const preview=await request(prefix+'/return-notes/preview',draft);
      const net=usage==='Used'?200:0, relief=7140-net*1.19;
      assert.equal(preview.chargeTotal,net); assert.equal(preview.reliefPaymentVoucher,paid?relief:0); checks+=2;
      const returned=await request(prefix+'/return-notes',draft,201);
      assert.equal(returned.chargeTotal,net); assert.equal(returned.reliefPaymentVoucher,paid?relief:0); checks+=2;
      const notes=await request(prefix+'/return-notes');
      assert.equal(notes[0].lines[0].allocations[0].chargeableDays,usage==='Used'?1:0); checks++;
      const credits=await request('/api/receivables/credit-notes?invoiceId='+invoice.id);
      assert.equal(credits.length,1); assert.equal(credits[0].grandTotalTtc,relief); checks+=2;
      const unchanged=await request('/api/receivables/invoices/'+invoice.id);
      assert.equal((unchanged.invoice||unchanged).grandTotalTtc,7140); checks++;
      if(paid) {
        const refund=await request('/api/payment-vouchers',{branchId:branch.id,kind:1,customerId:customer.id,contractId:contract.id,returnNoteId:returned.id,amount:relief,methodKey:'cash',purpose:'Same-day return'},201);
        const id=refund.voucher.id;
        await request(`/api/payment-vouchers/${id}/approve`,{});
        await request(`/api/payment-vouchers/${id}/pay`,{});
        const customers=await request('/api/catalog/customers');
        assert.equal(customers.find(c=>c.id===customer.id).balance,0); checks++;
      }
      console.log(`PASS ${usage}, ${paid?'paid and refunded':'unpaid'}, preview/save/credit/audit`);
    }
    console.log(`PASS ${checks} same-day SQL Server API assertions`);
  } finally { await request('/api/rentals/settings',settings,200,'PUT'); }
}
main().catch(e=>{console.error(e.message);process.exitCode=1;});

