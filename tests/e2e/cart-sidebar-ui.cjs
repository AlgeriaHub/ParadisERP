// Run from repo root: node tests/e2e/cart-sidebar-ui.cjs [build-directory] [before|after] [ar|fr|en] [touch|dark]
// Render the real UI with synthetic fixtures only; never contacts the production API.
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const root = path.resolve(process.argv[2] || 'AlgeriaPos.WebUI/dist/algeria-pos-web-ui/browser');
const output = path.resolve('artifacts/cart-sidebar-review', process.argv[5] || '');
fs.mkdirSync(output, { recursive: true });
const products = [{ id: 'product1', sku: 'LOC-001', barcode: '6130000000001', nameFr: 'Générateur de chantier', nameAr: 'مولد كهربائي', nameEn: 'Site generator', price: 500, taxRate: 0, unit: 'pcs', isActive: true, isRentable: true, trackStock: true, stockQty: 20, reorderLevel: 2, version: 1, isDeleted: false }];
const branches = [{ id: 'branch1', nameFr: 'Agence centrale', nameAr: 'الفرع الرئيسي', nameEn: 'Main branch', isActive: true }];
const customers = [{ id: 'customer1', code: 'DEMO-01', fullName: 'Client démonstration', kind: 'Person', balance: 0, creditLimit: 0 }];
const permissions = [...new Set(['payroll.manage', 'hr.attendance', ...[...fs.readFileSync('AlgeriaPos.Domain/Authorization/PermissionCatalog.cs', 'utf8').matchAll(/public const string \w+ = "([^"]+)"/g)].map(m => m[1])])];
const user = { id: 'ui-admin', username: 'admin', fullName: 'Administrateur', role: 'Admin', permissions, capabilities: ['customers.create'], isActive: true, branchLocked: false };
const server = http.createServer((req, res) => {
  const requested = new URL(req.url, 'http://localhost').pathname;
  const file = path.resolve(root, '.' + (requested === '/' ? '/index.html' : requested));
  if (!file.startsWith(root + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) { res.writeHead(404); return res.end(); }
  res.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.woff2': 'font/woff2', '.svg': 'image/svg+xml' })[path.extname(file)] || 'application/octet-stream');
  fs.createReadStream(file).pipe(res);
});

async function main() {
  const base = products[0];
  products.splice(0, products.length, ...Array.from({length:12}, (_, i) => ({...base, id:'product'+i, sku:'DEMO-'+i, barcode:'DEMO-'+i, nameAr:['مولد كهربائي','مثقاب كهربائي','سلم ألمنيوم','مضخة مياه','آلة قطع','ميزان ليزر','عربة نقل','خلاط إسمنت','كابل كهربائي','مصباح عمل','مطرقة','صندوق أدوات'][i], price:5, taxRate:19})));
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const origin = 'http://127.0.0.1:'+server.address().port;
  const browser = await chromium.launch({headless:true, channel:'chrome'});
  const context = await browser.newContext({viewport:{width:1440,height:900},serviceWorkers:'block',hasTouch:process.argv[5]==='touch'});
  const lang=process.argv[4]||'ar';
  await context.addInitScript(settings => { localStorage.setItem('pos.lang',settings.lang); localStorage.setItem('pos.appearance.v3',JSON.stringify({mode:settings.mode,theme:'atelier'})); },{lang,mode:process.argv[5]==='dark'?'dark':'light'});
  const page = await context.newPage();
  const errors=[]; page.on('pageerror', e=>errors.push(e.message));
  await page.route('**/api/**', async route => {
    const p=new URL(route.request().url()).pathname;
    let data;
    if(p==='/api/auth/ping') data={ok:true};
    if(p==='/api/auth/login') data={token:'synthetic-ui-only',expiresAtUtc:'2099-01-01T00:00:00Z',user,deviceCode:'C01',deviceBranchId:'branch1',deviceId:'device1'};
    if(p==='/api/sync/pull') data={cursor:1,hasMore:false,products,categories:[],customers,users:[user],branches,store:{id:'store1',nameFr:'Demo',nameAr:'متجر تجريبي',fiscalIds:{}}};
    if(p==='/api/catalog/products') data=products;
    if(p==='/api/catalog/customers') data=customers;
    if(p==='/api/catalog/branches') data=branches;
    if(['/api/catalog/categories','/api/treasury/payment-methods','/api/treasury/cash-boxes','/api/catalog/users'].includes(p)) data=[];
    if(data===undefined) return route.fulfill({status:503,json:{message:'Synthetic preview'}});
    await route.fulfill({json:data});
  });
  try {
    await page.goto(origin+'/#/login');
    await page.fill('#server',origin); await page.fill('#username','admin'); await page.fill('#password','synthetic-only');
    await page.click('button[type=submit]'); await page.waitForURL(/office|register/);
    await page.goto(origin+'/#/register');
    try { await page.locator('.tile-add').first().waitFor(); } catch(e) { console.log(await page.locator('body').innerText()); throw e; }
    for(let i=0;i<12;i++) await page.locator('.tile-add').nth(i).click();
    await page.evaluate(()=>document.fonts.ready);
    await page.locator('.cart').screenshot({path:path.join(output,(process.argv[3] || 'after')+'-'+lang+'-desktop.png')});
    const metrics=await page.evaluate(()=>{const b=document.querySelector('.cart-body'),r=b.getBoundingClientRect();return {itemsHeight:r.height,sidebarWidth:document.querySelector('.cart').getBoundingClientRect().width,visibleLines:[...b.querySelectorAll('.line')].filter(x=>{const q=x.getBoundingClientRect();return q.top>=r.top && q.bottom<=r.bottom}).length,total:document.querySelector('.totals-grand').textContent.trim()};});
    console.log((process.argv[3] || 'after'),JSON.stringify(metrics));
    fs.writeFileSync(path.join(output,(process.argv[3] || 'after')+'-'+lang+'-metrics.json'),JSON.stringify(metrics,null,2));
    if((process.argv[3] || 'after')==='after') {
      const pay=await page.locator('.cart-pay').boundingBox();
      await page.locator('.cart-body').evaluate(e=>e.scrollTop=e.scrollHeight);
      assert.deepEqual(await page.locator('.cart-pay').boundingBox(),pay,'Footer must not move when items scroll');
      assert(await page.locator('#cust').isVisible()); assert(await page.locator('#taxmode').isVisible());
      assert.equal(await page.locator('#disc-value').count(),0,'Discount editor starts collapsed');
      await page.locator('.disc-add-btn').click();
      await page.waitForFunction(()=>document.activeElement?.id==='disc-type');
      await page.selectOption('#disc-type','percent'); await page.fill('#disc-value','10'); await page.locator('#disc-value').press('Tab');
      const total=async()=>{await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))); return Number((await page.locator('.totals-grand dd').innerText()).replace(/[^0-9,.]/g,'').replace(',','.'));};
      assert.equal(await total(),54,'10% discount');
      await page.locator('.disc-done-btn').click();
      await page.locator('#disc-value').waitFor({state:'hidden'});
      await page.waitForFunction(()=>document.activeElement?.classList.contains('disc-edit-btn'));
      assert.equal(await page.locator('#disc-value').count(),0);
      assert.equal(await total(),54,'Closing editor preserves discount');
      assert(await page.locator('.disc-summary').innerText().then(t=>t.includes('10%')));
      await page.locator('.disc-edit-btn').click();
      await page.locator('#disc-value').waitFor();
      assert.equal(await page.inputValue('#disc-value'),'10');
      assert.equal(await total(),54,'Opening editor preserves discount');
      await page.locator('#taxmode').check();
      assert.equal(await total(),64.26,'VAT with percent discount');
      await page.selectOption('#disc-type','amount');
      await page.fill('#disc-value','5'); await page.locator('#disc-value').press('Tab');
      assert.equal(await total(),65.45,'Amount discount reduces the before-tax price');
      await page.selectOption('#cust','customer1');
      assert.equal(await total(),65.45,'Customer selection preserves price');
      await page.locator('.disc-remove-btn').click();
      await page.waitForFunction(()=>document.activeElement?.classList.contains('disc-add-btn'));
      assert.equal(await total(),71.4,'Remove discount');
      await page.locator('#taxmode').uncheck();
      assert.equal(await total(),60,'Untaxed state restored');
      await page.selectOption('#cust','');
      await page.locator('.pay').click(); await page.locator('.modal').waitFor();
      assert(await page.locator('.amount-figure').innerText().then(t=>t.includes('60')));
      await page.locator('.modal-close').click();
      await page.locator('.disc-add-btn').click();
      await page.selectOption('#disc-type','percent'); await page.fill('#disc-value','10'); await page.locator('#disc-value').press('Tab');
      await page.locator('.cart').screenshot({path:path.join(output,'after-'+lang+'-discount.png')});
      for(const width of [1366,1024]) {
        await page.setViewportSize({width,height:768});
        const rect=await page.locator('.pay').boundingBox(); assert(rect.y+rect.height<=768,'Desktop Pay remains in viewport');
        assert(await page.locator('.cart-body').evaluate(e=>e.clientHeight>=150));
        assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));
      }
      for(const width of [320,390,768]) {
        await page.setViewportSize({width,height:844});
        await page.locator('.sheet-handle').click();
        assert(await page.locator('.pay').isVisible());
        assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));
        assert(await page.locator('.cart-body').evaluate(e=>e.clientHeight>=100),'Expanded sheet gives items room');
        const rect=await page.locator('.pay').boundingBox(); assert(rect.y+rect.height<=844,'Pay is inside viewport');
        await page.screenshot({path:path.join(output,'after-'+lang+'-'+width+'.png')});
        await page.locator('.sheet-handle').click();
      }
    }
    assert.deepEqual(errors,[]);
  } finally {await browser.close();server.close();}
}
main().catch(e=>{console.error(e);server.close();process.exitCode=1;});
