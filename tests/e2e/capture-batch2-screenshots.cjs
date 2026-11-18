// Captures Batch 2 screenshots for Phase 4 redesign review:
// 1. Customers List (Arabic 1440px RTL, French 1440px LTR, Arabic 390px Mobile)
// 2. Customer Account Statement (Arabic 1440px RTL, French 1440px LTR)
// 3. Customer Quantity Statement (Arabic 1440px RTL, French 1440px LTR)
// 4. Finance: Payment Vouchers & Cash Boxes (Arabic 1440px RTL, French 1440px LTR)
// 5. Inventory: Receiving & Stock Transfers (Arabic 1440px RTL, French 1440px LTR, Arabic 390px Mobile)

const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');

const root = path.resolve(__dirname, '../..');
const out = path.join(root, 'artifacts/ui-redesign-checkpoint/batch2');
fs.mkdirSync(out, { recursive: true });

const webDist = path.join(root, 'AlgeriaPos.WebUI/dist/algeria-pos-web-ui/browser');

const permissions = [
  'admin.access',
  'dashboard.view',
  'catalog.view',
  'catalog.edit',
  'rentals.read',
  'rentals.contracts',
  'rentals.return',
  'rentals.issue',
  'sales.view',
  'customers.view',
  'customers.edit',
  'customers.accounts',
  'customers.opening_balance',
  'customer_statements.filter',
  'stock.view',
  'stock.receiving',
  'stock.transfers',
  'finance.view',
  'vouchers.view',
  'vouchers.create',
  'vouchers.approve',
  'receivables.view',
  'users.manage',
  'roles.manage',
];

const mockUser = {
  id: 'u-admin',
  username: 'admin',
  fullName: 'Chef de Parc / Directeur',
  role: 'Admin',
  isActive: true,
  permissions,
  capabilities: ['customer_statements.filter'],
  branchLocked: false,
};

const mockBranches = [
  { id: 'b-main', code: 'ALG', nameFr: 'Succursale Alger Centre', nameAr: 'فرع الجزائر الوسطى', isMain: true, isWarehouse: true, isActive: true, isOpenForOperation: true },
  { id: 'b-oran', code: 'ORN', nameFr: 'Succursale Oran', nameAr: 'فرع وهران', isMain: false, isWarehouse: false, isActive: true, isOpenForOperation: true },
];

const mockProducts = [
  { id: 'p1', sku: 'ART-0042', barcode: '6131234567890', nameFr: 'Panneau coffrage 0.76 x 3.00 m', nameAr: 'لوح قولبة 0.76 × 3.00 م', categoryName: 'Coffrage', price: 12500, costPrice: 9000, rentalRate: 120, stockQuantity: 45, minStockQuantity: 10, isRentable: true, isActive: true },
  { id: 'p2', sku: 'ART-0043', barcode: '6131234567891', nameFr: 'Étai métallique télescopique 3.50 m', nameAr: 'دعامة معدنية تلسكوبية 3.50 م', categoryName: 'Étaiement', price: 4800, costPrice: 3200, rentalRate: 80, stockQuantity: 120, minStockQuantity: 20, isRentable: true, isActive: true },
  { id: 'p3', sku: 'ART-0044', barcode: '6131234567892', nameFr: 'Cadre échafaudage acier 2.00 m', nameAr: 'إطار سقالة فولاذي 2.00 م', categoryName: 'Échafaudage', price: 8200, costPrice: 6000, rentalRate: 150, stockQuantity: 8, minStockQuantity: 15, isRentable: true, isActive: true },
  { id: 'p4', sku: 'ART-0045', barcode: '6131234567893', nameFr: 'Plancher acier galvanisé 3.00 m', nameAr: 'أرضية فولاذية مجلفنة 3.00 م', categoryName: 'Échafaudage', price: 9500, costPrice: 7200, rentalRate: 110, stockQuantity: 34, minStockQuantity: 10, isRentable: true, isActive: true },
];

const mockSuppliers = [
  { id: 'sup-1', code: 'FRN-001', name: 'Société Métallurgique Algérienne', phone: '021 55 44 33', email: 'commercial@sma.dz' },
  { id: 'sup-2', code: 'FRN-002', name: 'Équipements & Bâtiment Chéraga', phone: '023 11 22 33', email: 'ebc@equip-dz.com' },
];

const mockCustomers = [
  {
    id: 'cust-1',
    code: 'CLI-2026-0012',
    fullName: 'SARL Bâtiment Moderne Algérie',
    firstName: '',
    familyName: '',
    kind: 'Company',
    phone: '0550 12 34 56',
    email: 'contact@batiment-moderne.dz',
    address: '14 Rue Didouche Mourad, Alger',
    tradeRegister: '16/00-1234567B22',
    taxId: '002216012345678',
    balance: 142000,
    creditLimit: 500000,
    loyaltyPoints: 450,
    isActive: true,
    representative: {
      fullName: 'Mohamed Benali',
      idDocumentType: 'NationalId',
      idDocumentNumber: '198516010042',
      phone: '0661 22 33 44',
    },
  },
  {
    id: 'cust-2',
    code: 'CLI-2026-0013',
    fullName: 'Karim Belkacem',
    firstName: 'Karim',
    familyName: 'Belkacem',
    kind: 'Person',
    phone: '0770 98 76 54',
    email: 'k.belkacem@gmail.com',
    address: 'Zone Industrielle Rouiba, Alger',
    balance: 28000,
    creditLimit: 100000,
    loyaltyPoints: 120,
    isActive: true,
  },
  {
    id: 'cust-3',
    code: 'CLI-2026-0014',
    fullName: 'EURL Travaux Hydrauliques de l’Est',
    firstName: '',
    familyName: '',
    kind: 'Company',
    phone: '038 45 67 89',
    email: 'the.constantine@yahoo.fr',
    address: 'Bellevue, Constantine',
    balance: 560000,
    creditLimit: 500000,
    loyaltyPoints: 890,
    isActive: true,
  },
];

const mockCustomerStatement = {
  customerId: 'cust-1',
  customerCode: 'CLI-2026-0012',
  customerName: 'SARL Bâtiment Moderne Algérie',
  from: '2026-10-01T00:00:00Z',
  to: '2026-10-15T23:59:59Z',
  openingBalance: 50000,
  closingBalance: 142000,
  totalDebit: 142000,
  totalCredit: 50000,
  rows: [
    {
      date: '2026-10-02T10:00:00Z',
      kind: 'RentalIssue',
      sourceType: 'rental',
      sourceId: 'c-1001',
      documentNumber: 'LOC-2026-0042',
      invoiceNumber: 'FAC-2026-0089',
      description: 'Location matériel coffrage et étais métalliques',
      debit: 142000,
      credit: 0,
      balance: 192000,
      isReversal: false,
      paidAtTill: 0,
    },
    {
      date: '2026-10-05T14:30:00Z',
      kind: 'ReceiptVoucher',
      sourceType: 'receipt',
      sourceId: 'rec-1',
      documentNumber: 'REC-2026-0034',
      invoiceNumber: null,
      description: 'Versement acompte par virement bancaire',
      debit: 0,
      credit: 50000,
      balance: 142000,
      isReversal: false,
      paidAtTill: 0,
    },
  ],
};

const mockQuantityStatement = {
  customerId: 'cust-1',
  customerName: 'SARL Bâtiment Moderne Algérie',
  from: '2026-10-01T00:00:00Z',
  to: '2026-10-15T23:59:59Z',
  current: false,
  firstIssueOn: '2026-10-01T08:00:00Z',
  warnings: [],
  summary: {
    itemTypes: 3,
    issued: 50,
    returned: 6,
    missingDamaged: 0,
    withCustomer: 44,
    value: 142000,
    provisionalValue: 0,
  },
  items: [
    {
      productId: 'p1',
      sku: 'ART-0042',
      nameFr: 'Panneau coffrage 0.76 x 3.00 m',
      nameAr: 'لوح قولبة 0.76 × 3.00 م',
      opening: 0,
      issued: 10,
      returned: 1,
      missingDamaged: 0,
      transferNet: 0,
      remaining: 9,
      noRent: false,
      rates: [{ unitPrice: 120, kind: 'Day' }],
      value: 32400,
      negativeBalance: false,
      movements: [
        {
          date: '2026-10-01T08:30:00Z',
          type: 'Issue',
          quantity: 10,
          documentNumber: 'BON-SORTIE-001',
          contractNumber: 'LOC-2026-0042',
          branchId: 'b-main',
          siteName: 'Bab Ezzouar',
        },
        {
          date: '2026-10-08T14:30:00Z',
          type: 'Return',
          quantity: -1,
          documentNumber: 'BON-RETOUR-001',
          contractNumber: 'LOC-2026-0042',
          branchId: 'b-main',
          siteName: 'Bab Ezzouar',
        },
      ],
    },
    {
      productId: 'p2',
      sku: 'ART-0043',
      nameFr: 'Étai métallique télescopique 3.50 m',
      nameAr: 'دعامة معدنية تلسكوبية 3.50 م',
      opening: 0,
      issued: 25,
      returned: 5,
      missingDamaged: 0,
      transferNet: 0,
      remaining: 20,
      noRent: false,
      rates: [{ unitPrice: 80, kind: 'Day' }],
      value: 48000,
      negativeBalance: false,
      movements: [],
    },
    {
      productId: 'p4',
      sku: 'ART-0045',
      nameFr: 'Plancher acier galvanisé 3.00 m',
      nameAr: 'أرضية فولاذية مجلفنة 3.00 م',
      opening: 0,
      issued: 15,
      returned: 0,
      missingDamaged: 0,
      transferNet: 0,
      remaining: 15,
      noRent: false,
      rates: [{ unitPrice: 110, kind: 'Day' }],
      value: 49500,
      negativeBalance: false,
      movements: [],
    },
  ],
  contracts: [
    {
      contractId: 'c-1001',
      number: 'LOC-2026-0042',
      status: 'Open',
      branchId: 'b-main',
      siteName: 'Bab Ezzouar - Tour Résidentielle',
      value: 142000,
      provisionalValue: 0,
      lines: [
        {
          lineId: 'l-1',
          productId: 'p1',
          sku: 'ART-0042',
          nameFr: 'Panneau coffrage 0.76 x 3.00 m',
          nameAr: 'لوح قولبة 0.76 × 3.00 م',
          firstIssueOn: '2026-10-01T08:00:00Z',
          issued: 10,
          returned: 1,
          missingDamaged: 0,
          transferredIn: 0,
          transferredOut: 0,
          remaining: 9,
          noRent: false,
          rentUnitPrice: 120,
          rates: [],
          value: 32400,
        },
      ],
    },
  ],
};

const mockPaymentVouchers = [
  {
    id: 'pv-1',
    number: 'DEC-2026-0015',
    kind: 4, // supplierPayment
    expenseCategory: 'Achats de consommables et pièces',
    payeeName: 'Société Métallurgique Algérienne',
    branchId: 'b-main',
    branchName: 'Succursale Alger Centre',
    methodKey: 'Cash',
    methodName: 'Espèces',
    amount: 85000,
    status: 1, // Pending
    createdAtUtc: '2026-10-12T09:30:00Z',
  },
  {
    id: 'pv-2',
    number: 'DEC-2026-0016',
    kind: 1, // refund
    customerName: 'Karim Belkacem',
    returnNoteNumber: 'RET-2026-0089',
    branchId: 'b-main',
    branchName: 'Succursale Alger Centre',
    methodKey: 'Cash',
    methodName: 'Espèces',
    amount: 12000,
    status: 1, // Pending
    createdAtUtc: '2026-10-14T11:15:00Z',
  },
  {
    id: 'pv-3',
    number: 'DEC-2026-0017',
    kind: 2, // expense
    expenseCategory: 'Transport et logistique',
    payeeName: 'Transporteur Express Centre',
    branchId: 'b-main',
    branchName: 'Succursale Alger Centre',
    methodKey: 'BankTransfer',
    methodName: 'Virement bancaire',
    amount: 25000,
    status: 2, // Approved
    createdAtUtc: '2026-10-15T08:00:00Z',
  },
];

const mockCashBoxes = [
  { id: 'cb1', name: 'Caisse Principale (Coffre)', branchId: 'b-main', balance: 1485000, currency: 'DZD', isMain: true },
  { id: 'cb2', name: 'Caisse Comptoir Vente', branchId: 'b-main', balance: 92400, currency: 'DZD', isMain: false },
  { id: 'cb3', name: 'Caisse Annexe Oran', branchId: 'b-oran', balance: 340000, currency: 'DZD', isMain: false },
];

const mockTransfers = [
  {
    id: 'tr-1',
    number: 'TR-2026-0021',
    status: 'Dispatched',
    fromBranchId: 'b-main',
    fromBranchName: 'Succursale Alger Centre',
    toBranchId: 'b-oran',
    toBranchName: 'Succursale Oran',
    note: 'Transfert de réapprovisionnement',
    dispatchedAtUtc: '2026-10-14T08:00:00Z',
    dispatchedByName: 'Administrateur',
    closedAtUtc: null,
    closedByName: null,
    totalQuantity: 50,
    lines: [
      {
        id: 'l-1',
        productId: 'p1',
        sku: 'ART-0042',
        nameAr: 'لوح قولبة 0.76 × 3.00 م',
        nameFr: 'Panneau coffrage 0.76 x 3.00 m',
        nameEn: 'Formwork panel',
        qtyDispatched: 20,
        qtyReceived: 0,
        qtyLost: 0,
        qtyReturned: 0,
        inTransit: 20,
        unitCost: 9000,
        qtyShortPending: 0,
        qtyShortReported: 0,
        qtyShortConfirmed: 0,
        qtyShortRejected: 0,
      },
      {
        id: 'l-2',
        productId: 'p2',
        sku: 'ART-0043',
        nameAr: 'دعامة معدنية تلسكوبية 3.50 م',
        nameFr: 'Étai métallique télescopique 3.50 m',
        nameEn: 'Props',
        qtyDispatched: 30,
        qtyReceived: 0,
        qtyLost: 0,
        qtyReturned: 0,
        inTransit: 30,
        unitCost: 3200,
        qtyShortPending: 0,
        qtyShortReported: 0,
        qtyShortConfirmed: 0,
        qtyShortRejected: 0,
      },
    ],
    events: [],
  },
  {
    id: 'tr-2',
    number: 'TR-2026-0020',
    status: 'Received',
    fromBranchId: 'b-oran',
    fromBranchName: 'Succursale Oran',
    toBranchId: 'b-main',
    toBranchName: 'Succursale Alger Centre',
    note: 'Retour surplus',
    dispatchedAtUtc: '2026-10-10T09:00:00Z',
    dispatchedByName: 'Chef de dépôt Oran',
    closedAtUtc: '2026-10-11T16:00:00Z',
    closedByName: 'Magasinier Alger',
    totalQuantity: 15,
    lines: [
      {
        id: 'l-3',
        productId: 'p4',
        sku: 'ART-0045',
        nameAr: 'أرضية فولاذية مجلفنة 3.00 م',
        nameFr: 'Plancher acier galvanisé 3.00 m',
        nameEn: 'Steel floor',
        qtyDispatched: 15,
        qtyReceived: 15,
        qtyLost: 0,
        qtyReturned: 0,
        inTransit: 0,
        unitCost: 7200,
        qtyShortPending: 0,
        qtyShortReported: 0,
        qtyShortConfirmed: 0,
        qtyShortRejected: 0,
      },
    ],
    events: [],
  },
];

const mockPaymentMethods = [
  { id: 'm-cash', key: 'Cash', code: 'Cash', nameFr: 'Espèces', nameAr: 'نقداً', nameEn: 'Cash', behavior: 'Cash', isActive: true },
  { id: 'm-cheque', key: 'Cheque', code: 'Cheque', nameFr: 'Chèque', nameAr: 'شيك', nameEn: 'Cheque', behavior: 'BankCheque', isActive: true },
  { id: 'm-transfer', key: 'BankTransfer', code: 'BankTransfer', nameFr: 'Virement bancaire', nameAr: 'تحويل بنكي', nameEn: 'Transfer', behavior: 'BankTransfer', isActive: true },
];

async function main() {
  console.log(`\nCapturing Batch 2 Screens...`);

  const server = http.createServer((req, res) => {
    const requested = decodeURIComponent(new URL(req.url, 'http://127.0.0.1:5211').pathname);
    const filePath = path.resolve(webDist, '.' + (requested === '/' ? '/index.html' : requested));
    if (!filePath.startsWith(webDist + path.sep) || !fs.existsSync(filePath) || !fs.statSync(filePath).isFile()) {
      const fallback = path.join(webDist, 'index.html');
      res.writeHead(200, { 'Content-Type': 'text/html' });
      fs.createReadStream(fallback).pipe(res);
      return;
    }
    const mimeMap = {
      '.html': 'text/html',
      '.js': 'text/javascript',
      '.css': 'text/css',
      '.json': 'application/json',
      '.woff2': 'font/woff2',
      '.svg': 'image/svg+xml',
      '.png': 'image/png',
    };
    res.setHeader('Content-Type', mimeMap[path.extname(filePath)] || 'application/octet-stream');
    fs.createReadStream(filePath).pipe(res);
  });

  await new Promise((resolve) => server.listen(5211, '127.0.0.1', resolve));

  const browser = await chromium.launch({ headless: true });

  try {
    const context = await browser.newContext({
      viewport: { width: 1440, height: 960 },
      serviceWorkers: 'block',
      timezoneId: 'Africa/Algiers',
    });
    const page = await context.newPage();

    await page.route('**/api/**', async (route) => {
      const url = new URL(route.request().url());
      const p = url.pathname;

      if (p === '/api/auth/login') {
        return route.fulfill({
          json: {
            token: 'mock-jwt-token',
            expiresAtUtc: '2099-01-01T00:00:00Z',
            user: mockUser,
            deviceCode: 'OFFICE-01',
            deviceBranchId: 'b-main',
          },
        });
      }
      if (p === '/api/catalog/branches') return route.fulfill({ json: mockBranches });
      if (p === '/api/catalog/products') return route.fulfill({ json: mockProducts });
      if (p === '/api/catalog/suppliers') return route.fulfill({ json: mockSuppliers });
      if (p === '/api/catalog/customers') return route.fulfill({ json: mockCustomers });
      if (p.startsWith('/api/catalog/customers/cust-1')) return route.fulfill({ json: mockCustomers[0] });
      if (p.startsWith('/api/customers/') && p.endsWith('/sites')) {
        return route.fulfill({ json: [{ id: 's-1', name: 'Bab Ezzouar - Tour Résidentielle' }] });
      }
      if (p.startsWith('/api/catalog/payment-methods')) return route.fulfill({ json: mockPaymentMethods });
      if (p.startsWith('/api/receivables/statements')) return route.fulfill({ json: mockCustomerStatement });
      if (p.startsWith('/api/rentals/quantity-statement')) return route.fulfill({ json: mockQuantityStatement });
      if (p.startsWith('/api/payment-vouchers')) {
        const statusParam = url.searchParams.get('status');
        if (statusParam) {
          const s = parseInt(statusParam, 10);
          return route.fulfill({ json: mockPaymentVouchers.filter((v) => v.status === s) });
        }
        return route.fulfill({ json: mockPaymentVouchers });
      }
      if (p.startsWith('/api/treasury/cash-boxes')) return route.fulfill({ json: mockCashBoxes });
      if (p.startsWith('/api/catalog/stock/transfers')) return route.fulfill({ json: mockTransfers });
      if (p.startsWith('/api/catalog/stock/availability')) {
        return route.fulfill({
          json: [
            { branchId: 'b-main', branchName: 'Succursale Alger Centre', available: 45, onRent: 10, maintenance: 2, scrap: 0, owned: 57 },
            { branchId: 'b-oran', branchName: 'Succursale Oran', available: 15, onRent: 5, maintenance: 0, scrap: 0, owned: 20 },
          ],
        });
      }
      if (p.startsWith('/api/receivables/ageing')) {
        return route.fulfill({
          json: {
            asOf: '2026-10-15T00:00:00Z',
            total: 730000,
            current: 560000,
            days1To30: 142000,
            days31To60: 28000,
            days61To90: 0,
            over90: 0,
            creditBalance: 0,
            ledgerBalance: 730000,
            uninvoiced: 0,
            customers: [],
          },
        });
      }
      if (p.startsWith('/api/receivables/invoices')) return route.fulfill({ json: [] });
      if (p.startsWith('/api/receivables/receipts')) return route.fulfill({ json: [] });
      if (p.startsWith('/api/receivables/credit-notes')) return route.fulfill({ json: [] });
      if (p.startsWith('/api/rentals/contracts')) return route.fulfill({ json: [] });

      return route.fulfill({ json: [] });
    });

    async function setLanguage(lang) {
      await page.evaluate((l) => localStorage.setItem('pos.lang', l), lang);
      await page.reload();
      await page.waitForTimeout(600);
    }

    // 1. Sign in
    await page.goto('http://127.0.0.1:5211/#/login');
    await page.evaluate(() => localStorage.setItem('pos.lang', 'ar'));
    await page.reload();
    await page.fill('#server', 'http://127.0.0.1:5211');
    await page.fill('#username', 'admin');
    await page.fill('#password', 'AnyPassword123');
    await page.click('button[type=submit]');
    await page.waitForURL(/office|register|dashboard/);

    // =============================================================
    // BATCH 2 - SCREEN 1: CUSTOMERS LIST
    // =============================================================
    console.log('Capturing Batch 2 Screen 1: Customers List...');
    await page.setViewportSize({ width: 1440, height: 960 });
    await page.goto('http://127.0.0.1:5211/#/backoffice/customers/list');
    await setLanguage('ar');
    await page.waitForSelector('table.bo-data tbody tr');
    await page.waitForTimeout(600);
    await page.screenshot({ path: path.join(out, '01-customers-list-ar-1440.png') });

    await setLanguage('fr');
    await page.waitForSelector('table.bo-data tbody tr');
    await page.waitForTimeout(600);
    await page.screenshot({ path: path.join(out, '01-customers-list-fr-1440.png') });

    await page.setViewportSize({ width: 390, height: 844 });
    await setLanguage('ar');
    await page.waitForSelector('table.bo-data tbody tr');
    await page.waitForTimeout(600);
    await page.screenshot({ path: path.join(out, '01-customers-list-ar-390.png') });

    // =============================================================
    // BATCH 2 - SCREEN 2: CUSTOMER ACCOUNT STATEMENT
    // =============================================================
    console.log('Capturing Batch 2 Screen 2: Customer Account Statement...');
    await page.setViewportSize({ width: 1440, height: 960 });
    await page.goto('http://127.0.0.1:5211/#/backoffice/customers/accounts?customer=cust-1');
    await setLanguage('ar');
    await page.waitForSelector('.bo-metrics, table.bo-data');
    await page.waitForTimeout(700);
    await page.screenshot({ path: path.join(out, '02-customer-account-statement-ar-1440.png') });

    await setLanguage('fr');
    await page.waitForSelector('.bo-metrics, table.bo-data');
    await page.waitForTimeout(700);
    await page.screenshot({ path: path.join(out, '02-customer-account-statement-fr-1440.png') });

    // =============================================================
    // BATCH 2 - SCREEN 3: CUSTOMER QUANTITY STATEMENT
    // =============================================================
    console.log('Capturing Batch 2 Screen 3: Customer Quantity Statement...');
    await page.goto('http://127.0.0.1:5211/#/backoffice/customers/quantities');
    await setLanguage('ar');
    await page.waitForSelector('#qs-customer');
    await page.selectOption('#qs-customer', { label: 'CLI-2026-0012 — SARL Bâtiment Moderne Algérie' });
    await page.waitForTimeout(400);
    await page.click('button.btn-primary');
    await page.waitForSelector('table.qs-items');
    await page.waitForTimeout(700);
    await page.screenshot({ path: path.join(out, '03-customer-quantity-statement-ar-1440.png') });

    await setLanguage('fr');
    await page.waitForSelector('#qs-customer');
    await page.selectOption('#qs-customer', { label: 'CLI-2026-0012 — SARL Bâtiment Moderne Algérie' });
    await page.waitForTimeout(400);
    await page.click('button.btn-primary');
    await page.waitForSelector('table.qs-items');
    await page.waitForTimeout(700);
    await page.screenshot({ path: path.join(out, '03-customer-quantity-statement-fr-1440.png') });

    // =============================================================
    // BATCH 2 - SCREEN 4: FINANCE (VOUCHERS & CASH BOXES)
    // =============================================================
    console.log('Capturing Batch 2 Screen 4: Finance (Payment Vouchers & Cash Boxes)...');
    await page.goto('http://127.0.0.1:5211/#/backoffice/finance/vouchers');
    await setLanguage('ar');
    await page.waitForSelector('table.ui-table, .ui-surface');
    await page.waitForTimeout(700);
    await page.screenshot({ path: path.join(out, '04-finance-vouchers-ar-1440.png') });

    await setLanguage('fr');
    await page.waitForSelector('table.ui-table, .ui-surface');
    await page.waitForTimeout(700);
    await page.screenshot({ path: path.join(out, '04-finance-vouchers-fr-1440.png') });

    await page.goto('http://127.0.0.1:5211/#/backoffice/finance/cash-boxes');
    await setLanguage('ar');
    await page.waitForSelector('table.ui-table, .ui-surface');
    await page.waitForTimeout(700);
    await page.screenshot({ path: path.join(out, '04-finance-cash-boxes-ar-1440.png') });

    // =============================================================
    // BATCH 2 - SCREEN 5: INVENTORY (RECEIVING & TRANSFERS)
    // =============================================================
    console.log('Capturing Batch 2 Screen 5: Inventory (Receiving & Transfers)...');
    await page.goto('http://127.0.0.1:5211/#/backoffice/inventory/receiving');
    await setLanguage('ar');
    await page.waitForSelector('.bo-panel, .rule');
    await page.waitForTimeout(700);
    await page.screenshot({ path: path.join(out, '05-inventory-receiving-ar-1440.png') });

    await setLanguage('fr');
    await page.waitForSelector('.bo-panel, .rule');
    await page.waitForTimeout(700);
    await page.screenshot({ path: path.join(out, '05-inventory-receiving-fr-1440.png') });

    await page.goto('http://127.0.0.1:5211/#/backoffice/inventory/transfers');
    await setLanguage('ar');
    await page.waitForSelector('table.bo-data, .bo-panel');
    await page.waitForTimeout(700);
    await page.screenshot({ path: path.join(out, '05-inventory-transfers-ar-1440.png') });

    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForTimeout(600);
    await page.screenshot({ path: path.join(out, '05-inventory-transfers-ar-390.png') });

    console.log('Successfully captured all Batch 2 screenshots!');
  } finally {
    await browser.close();
    server.close();
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
