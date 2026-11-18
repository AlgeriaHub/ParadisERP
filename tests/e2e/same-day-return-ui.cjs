const { requiredPassword } = require('./test-credentials.cjs');
const testPassword = requiredPassword();
// Browser verification against a local synthetic API; saves are deliberately rejected.
const {chromium}=require('playwright');
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),http=require('node:http');
const api=process.env.BASE || 'http://127.0.0.1:5187',root=path.resolve(__dirname,'../..');
if (!/^http:\/\/(127\.0\.0\.1|localhost):/.test(api)) throw Error('Local test API required');
const out=path.join(root,'artifacts/same-day-ui');fs.mkdirSync(out,{recursive:true});
let token, checks=0;
async function req(p,body,method=body?'POST':'GET') {
 const r=await fetch(api+p,{method,headers:{'content-type':'application/json',...(token?{authorization:`Bearer ${token}`}:{})},body:body?JSON.stringify(body):undefined});
 const data=await r.json(); if(!r.ok)throw Error(`${p}: ${JSON.stringify(data)}`); return data;
}
async function main(){
 token=(await req('/api/auth/login',{username:'admin',password:testPassword})).token;
 const branch=(await req('/api/catalog/branches')).find(x=>x.isMain),product=(await req('/api/catalog/products')).find(x=>x.trackStock);
 const customer=await req('/api/catalog/customers',{fullName:'Synthetic same-day UI '+Date.now(),kind:'Person',creditLimit:100000,phone:'0555 00 00 00',birthDate:'1985-04-12T00:00:00Z',idDocumentNumber:'109 876 543',idDocumentType:'nationalCard',idDocumentIssuer:'Synthetic',idDocumentIssuedOn:'2019-06-01'});
 const site=await req(`/api/customers/${customer.id}/sites`,{name:'Synthetic site'});
 const contract=await req('/api/rentals/contracts',{customerId:customer.id,branchId:branch.id,siteId:site.id,lines:[{productId:product.id,qty:1,durationDays:30,rentUnitPrice:100,damageUnitPrice:2000}]});
 await req(`/api/rentals/contracts/${contract.id}/issue-notes`,{branchId:branch.id,customerTransport:true,issuedAtUtc:'2026-10-14T06:00:00Z',lines:[{contractLineId:contract.lines[0].id,qty:1}]});
 const web=path.join(root,'ParadiERP.WebUI/dist/paradierp-web-ui/browser');
 const server=http.createServer(async(r,s)=>{
  if(r.url.startsWith('/api/')||r.url==='/health'){
   const chunks=[];for await(const chunk of r)chunks.push(chunk);
   const headers={...r.headers};delete headers.host;delete headers.connection;delete headers['content-length'];
   const back=await fetch(api+r.url,{method:r.method,headers,body:['GET','HEAD'].includes(r.method)?undefined:Buffer.concat(chunks)});
   s.writeHead(back.status,{'content-type':back.headers.get('content-type')||'application/json'});s.end(Buffer.from(await back.arrayBuffer()));return;
  }
  const file=path.resolve(web,'.'+(r.url==='/'?'/index.html':decodeURIComponent(r.url.split('?')[0])));
  if(!file.startsWith(web+path.sep)||!fs.existsSync(file)||!fs.statSync(file).isFile()){s.writeHead(404);s.end();return;}
  s.setHeader('content-type',({'.html':'text/html','.js':'text/javascript','.css':'text/css','.json':'application/json','.woff2':'font/woff2'})[path.extname(file)]||'application/octet-stream');fs.createReadStream(file).pipe(s);
 });
 await new Promise(resolve=>server.listen(5190,'127.0.0.1',resolve));
 const browser=await chromium.launch({headless:true,channel:'chrome'});
 try{
 for(const lang of ['fr','ar']){
  const context=await browser.newContext({viewport:{width:1440,height:1000},serviceWorkers:'block'});
  await context.addInitScript(l=>localStorage.setItem('pos.lang',l),lang);
  const page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto('http://127.0.0.1:5190/#/login');
  await page.fill('#server','http://127.0.0.1:5190');await page.fill('#username','admin');await page.fill('#password',testPassword);await page.click('button[type=submit]');
  await page.waitForURL(/office|register/);
  await page.goto(`http://127.0.0.1:5190/#/backoffice/rentals/contracts/${contract.id}/return`);
  await page.waitForSelector('app-rental-return-note');
  await page.fill('#rr-date','2026-10-14');await page.fill('#rr-time','14:00');await page.check('#rr-by-customer');
  await page.fill('#rr-ok'+contract.lines[0].id,'1');
  await page.waitForSelector('.usage-choice');
  const used=page.locator('.usage-choice input[value="Used"]'),unused=page.locator('.usage-choice input[value="NotUsed"]');
  assert.equal(await used.isChecked(),false);assert.equal(await unused.isChecked(),false);checks+=2;
  const preview=page.waitForResponse(r=>r.url().endsWith('/return-notes/preview')&&r.request().postDataJSON().lines[0]?.sameDayUsage==='Used');
  await used.check();assert.equal((await (await preview).json()).chargeTotal,100);checks++;
  const zero=page.waitForResponse(r=>r.url().endsWith('/return-notes/preview')&&r.request().postDataJSON().lines[0]?.sameDayUsage==='NotUsed');
  await unused.check();assert.equal((await (await zero).json()).chargeTotal,0);checks++;
  for(const width of [1440,768,390]){
   await page.setViewportSize({width,height:1000});
   await page.locator('.usage-choice').scrollIntoViewIfNeeded();
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),true);checks++;
   await page.screenshot({path:path.join(out,`${lang}-${width}.png`),fullPage:true});
  }
  await page.route('**/return-notes',route=>route.request().method()==='POST'?route.fulfill({status:400,json:{message:'Synthetic validation refusal'}}):route.continue());
  const save=page.locator('app-rental-return-note .ui-actionbar .btn-primary');
  await save.click();
  const confirm=page.locator('app-confirm-dialog button.btn-primary');
  if(await confirm.count())await confirm.click();
  await page.waitForSelector('app-rental-return-note .notice.danger');
  assert.equal(await unused.isChecked(),true);assert.equal(await page.inputValue('#rr-ok'+contract.lines[0].id),'1');checks+=2;
  assert.deepEqual(errors,[]);checks++;
  await context.close();console.log(`PASS ${lang} choices, server pricing, retained draft, desktop/tablet/phone`);
 }
 console.log(`PASS ${checks} same-day browser assertions`);
 }finally{await browser.close();await new Promise(resolve=>server.close(resolve));}
}
main().catch(e=>{console.error(e);process.exitCode=1;});
