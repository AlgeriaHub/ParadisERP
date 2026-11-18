const testPassword = require('node:crypto').randomBytes(24).toString('hex');
// Local mocked browser verification: no live API writes.
const {chromium}=require('playwright');
const fs=require('fs'),path=require('path');
const out=path.resolve('artifacts/design/rental-builder');fs.mkdirSync(out,{recursive:true});
const before=process.argv.includes('--before');
let assertions=0;const check=(name,ok)=>{if(!ok)throw Error(name);assertions++;console.log('PASS',name)};
const products=Array.from({length:20},(_,i)=>({id:`p${i+1}`,sku:`RENT-${String(i+1).padStart(3,'0')}`,barcode:`613000000${String(i+1).padStart(4,'0')}`,nameFr:`GÃ©nÃ©rateur chantier ${i+1}`,nameAr:`Ù…ÙˆÙ„Ø¯ ÙƒÙ‡Ø±Ø¨Ø§Ø¦ÙŠ ${i+1}`,nameEn:`Site generator ${i+1}`,isActive:true,isRentable:true,trackStock:true,stockQty:200,price:5,taxRate:0,unit:'pcs'}));
async function main(){
const root=path.resolve('ParadiERP.WebUI/dist/paradierp-web-ui/browser');
const server=require('http').createServer((req,res)=>{
 const requested=decodeURIComponent(new URL(req.url,'http://localhost').pathname);
 const file=path.resolve(root,'.'+(requested==='/'?'/index.html':requested));
 if(!file.startsWith(root+path.sep)||!fs.existsSync(file)||!fs.statSync(file).isFile()){res.writeHead(404);return res.end();}
 const types={'.html':'text/html','.js':'text/javascript','.css':'text/css','.json':'application/json','.woff2':'font/woff2','.svg':'image/svg+xml'};
 res.setHeader('Content-Type',types[path.extname(file)]||'application/octet-stream');fs.createReadStream(file).pipe(res);
});
await new Promise(resolve=>server.listen(5099,'127.0.0.1',resolve));
const permissions=[...fs.readFileSync('ParadiERP.Domain/Authorization/PermissionCatalog.cs','utf8').matchAll(/public const string \w+ = "([^"]+)"/g)].map(m=>m[1]);
const user={id:'ui-test-admin',username:'admin',fullName:'Administrateur',role:'Admin',isActive:true,permissions,capabilities:['customers.create'],branchLocked:false};
products.forEach((p,i)=>{p.nameFr=`G\u00e9n\u00e9rateur chantier ${i+1}`;p.nameAr=`\u0645\u0648\u0644\u062f \u0643\u0647\u0631\u0628\u0627\u0626\u064a ${i+1}`;});
const browser=await chromium.launch({headless:true,channel:'chrome'});
for(const lang of ['fr','ar']){
 const context=await browser.newContext({viewport:{width:1440,height:1000},serviceWorkers:'block'});
 await context.addInitScript(l=>localStorage.setItem('pos.lang',l),lang);
 const page=await context.newPage();page.on('pageerror',e=>console.log('PAGEERROR',e.message));
 await page.route('**/api/**',async route=>{
  const p=new URL(route.request().url()).pathname;
  let data=[];
  if(p==='/api/auth/login')return route.fulfill({json:{token:'local-mock-only',expiresAtUtc:'2099-01-01T00:00:00Z',user,deviceCode:'C01',deviceBranchId:'branch1'}});
  if(p==='/api/sync/pull')return route.fulfill({json:{cursor:1,hasMore:false,products,categories:[],customers:[],users:[user],branches:[]}});
  if(p==='/api/catalog/products')data=products;
  if(p==='/api/catalog/customers')data=[{id:'customer1',code:'DEMO-01',fullName:'Client demonstration',kind:'Person',creditLimit:0,balance:0}];
  if(p==='/api/catalog/branches')data=[{id:'branch1',nameFr:'Agence centrale',nameAr:'Ø§Ù„ÙØ±Ø¹ Ø§Ù„Ø±Ø¦ÙŠØ³ÙŠ',nameEn:'Main branch',isActive:true}];
  if(p==='/api/rentals/settings')data={overdueMultiplier:2,depositEnabled:false,taxRate:0};
  if(p==='/api/rentals/settings/products')data=products.map(p=>({productId:p.id,rentalUnitPrice:5,rentalDamageUnitPrice:1000,overdueMultiplier:null}));
  if(p==='/api/catalog/store/documents')data={contractWordings:[]};
  if(p==='/api/rentals/contracts'&&route.request().method()==='POST')return route.fulfill({status:400,json:{message:'Validation test: review rental lines.'}});
  if(p==='/api/catalog/branches')data[0].nameAr='الفرع الرئيسي';
  await route.fulfill({json:data});
 });
 await page.goto('http://localhost:5099/#/login');
 await page.fill('#server','http://localhost:5099');await page.fill('#username','admin');await page.fill('#password',testPassword);await page.click('button[type=submit]');
 await page.waitForURL(/office|register/);
 await page.goto('http://localhost:5099/#/backoffice/rentals/contracts/new');
 await page.waitForSelector('app-rental-contract-form');
 await page.evaluate(()=>document.fonts.ready);
 if(!before){
  await page.locator('.items-heading button').click();
  for(let i=0;i<15;i++){
   await page.fill('#rf-scan',products[i].barcode);await page.press('#rf-scan','Enter');
   await page.waitForFunction(n=>document.querySelectorAll('.rental-row').length===n,i+1);
   check(`${lang} picker retains focus ${i+1}`,await page.locator('#rf-scan').evaluate(e=>e===document.activeElement&&e.value===''));
   if([1,5,15].includes(i+1)){
    await page.locator('#rf-scan').press('Escape');
    await page.screenshot({path:path.join(out,`after-${lang}-1440-${i+1}-items.png`),fullPage:true});
   }
  }
  await page.fill('#rf-scan',products[0].barcode);await page.press('#rf-scan','Enter');
  await page.waitForFunction(()=>ng.getComponent(document.querySelector('app-rental-contract-form')).lines()[0].qty===2);
  check(`${lang} repeat scan aggregates independent item quantity`,await page.locator('.rental-row').count()===15);
  await page.locator('.rental-add-panel > button').click();
  await page.locator('#rf-edit8').click();await page.fill('#rf-q8','100');await page.fill('#rf-d8','30');await page.fill('#rf-r8','5');
  check(`${lang} middle editor immediate total`,await page.evaluate(()=>{const c=ng.getComponent(document.querySelector('app-rental-contract-form'));return c.linePeriod(c.lines().find(x=>x.key===8))===15000;}));
  await page.locator('.editor-close').click();
  await page.locator('#rf-edit9').click();await page.locator('#rf-parent9').selectOption({index:8});
  check(`${lang} linked duration protected`,await page.locator('#rf-d9').isDisabled());await page.locator('.editor-close').click();
  await page.locator('.rental-row').last().locator('summary').click();await page.locator('.rental-row').last().locator('.row-menu button').last().click();
  await page.locator('app-confirm-dialog .btn-danger-solid').click();await page.waitForFunction(()=>document.querySelectorAll('.rental-row').length===14);
  check(`${lang} removal confirms and preserves remaining lines`,await page.locator('.rental-row').count()===14);
  await page.evaluate(()=>{const c=ng.getComponent(document.querySelector('app-rental-contract-form'));c.customerId.set('customer1');c.branchId.set('branch1');c.notes.set('Preserve this entered note');ng.applyChanges(c);});
  const snapshot=await page.evaluate(()=>{const c=ng.getComponent(document.querySelector('app-rental-contract-form'));return JSON.stringify({lines:c.lines(),notes:c.notes(),customer:c.customerId()});});
  await page.evaluate(async()=>{const c=ng.getComponent(document.querySelector('app-rental-contract-form'));await c.save();ng.applyChanges(c);});
  check(`${lang} validation preserves all data`,snapshot===await page.evaluate(()=>{const c=ng.getComponent(document.querySelector('app-rental-contract-form'));return JSON.stringify({lines:c.lines(),notes:c.notes(),customer:c.customerId()});}));
  console.log('VALIDATION',await page.evaluate(()=>{const c=ng.getComponent(document.querySelector('app-rental-contract-form'));return {error:c.error(),problem:c.problem()};}));
  await page.evaluate(async()=>{const c=ng.getComponent(document.querySelector('app-rental-contract-form'));c.siteRequired.set(false);await c.save();ng.applyChanges(c);});
  check(`${lang} server validation reached`,await page.evaluate(()=>!!ng.getComponent(document.querySelector('app-rental-contract-form')).error()));
  check(`${lang} server rejection preserves all data`,snapshot===await page.evaluate(()=>{const c=ng.getComponent(document.querySelector('app-rental-contract-form'));return JSON.stringify({lines:c.lines(),notes:c.notes(),customer:c.customerId()});}));
  await page.screenshot({path:path.join(out,`after-${lang}-validation-preserved.png`),fullPage:true});
  await page.evaluate(()=>{const c=ng.getComponent(document.querySelector('app-rental-contract-form'));c.error.set(null);c.attempts.set(0);c.notes.set('');c.addedNote.set('');ng.applyChanges(c);});
 }
 await page.evaluate(ps=>{const c=ng.getComponent(document.querySelector('app-rental-contract-form'));c.lines.set(ps.slice(0,5).map((p,i)=>({key:i+1,productId:p.id,qty:i===0?100:2,durationDays:30,rentUnitPrice:5,damageUnitPrice:1000,overdueUnitPrice:null,parentKey:null})));c.nextKey=6;c.customerId.set('customer1');c.branchId.set('branch1');ng.applyChanges(c);},products);
 for(const width of [1440,768,390]){await page.setViewportSize({width,height:1000});await page.screenshot({path:path.join(out,`${before?'before':'after'}-${lang}-${width}-5-items.png`),fullPage:true});check(`${lang} ${width}px no horizontal overflow`,await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));if(!before){await page.locator('#rf-edit3').click();await page.screenshot({path:path.join(out,`after-${lang}-${width}-editor.png`)});check(`${lang} ${width}px dialog fits`,await page.locator('.rental-editor').evaluate(e=>{const r=e.getBoundingClientRect();return r.left>=0&&r.right<=innerWidth}));await page.locator('.editor-close').click();}}
 await context.close();
}
await browser.close();
await new Promise(resolve=>server.close(resolve));
console.log(`${assertions} assertions passed`);
}
main().catch(e=>{console.error(e);process.exit(1)});

