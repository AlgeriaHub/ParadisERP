import Dexie, { Table } from 'dexie';
import {
  Branch, Category, Customer, DeviceMeta, GiftCard, LanguagePack, PaymentMethodDef, PosUser, Product, Sale, Store,
} from './models';

/**
 * The terminal's local database. Everything the register needs to sell is here, so a sale
 * never touches the network: the catalog is a replica of the server, and sales are written
 * locally first and drained from the outbox when a connection comes back.
 */
export class PosDatabase extends Dexie {
  products!: Table<Product, string>;
  categories!: Table<Category, string>;
  customers!: Table<Customer, string>;
  users!: Table<PosUser, string>;
  stores!: Table<Store, string>;
  branches!: Table<Branch, string>;
  sales!: Table<Sale, string>;
  meta!: Table<DeviceMeta, string>;
  languagePacks!: Table<LanguagePack, string>;
  giftCards!: Table<GiftCard, string>;
  paymentMethods!: Table<PaymentMethodDef, string>;

  constructor() {
    super('algeria-pos');

    this.version(1).stores({
      products: 'id, sku, barcode, categoryId, isActive, version',
      categories: 'id, sortOrder, version',
      customers: 'id, code, version',
      users: 'id, username, version',
      stores: 'id, version',
      // outboxState is the index the sync engine scans to find unsent sales.
      sales: 'id, reference, soldAtUtc, outboxState, localSeq, customerId',
      meta: 'key',
    });

    this.version(2).stores({
      branches: 'id, version',
    });

    // Country profiles: fiscal identifiers became a key/value map and the customer's NIF a
    // generic tax ID. Rows are reshaped in place so a till that is offline keeps working.
    this.version(3)
      .stores({})
      .upgrade(async (tx) => {
        await tx
          .table('stores')
          .toCollection()
          .modify((s: Record<string, unknown>) => {
            s['fiscalIds'] = Object.fromEntries(
              (['nif', 'nis', 'rc', 'ai'] as const).filter((k) => s[k]).map((k) => [k, s[k]]),
            );
            s['requireCustomerTaxId'] = !!s['requireCustomerNif'];
            for (const k of ['nif', 'nis', 'rc', 'ai', 'currencyCode', 'pricesIncludeTax', 'requireCustomerNif']) delete s[k];
          });
        await tx
          .table('customers')
          .toCollection()
          .modify((c: Record<string, unknown>) => {
            c['taxId'] = c['nif'] ?? null;
            delete c['nif'];
          });
      });

    // Operator-uploaded translations, kept so an extra language also works offline.
    this.version(4).stores({
      languagePacks: 'code',
    });

    // Gift cards, replicated like the catalog so a till can check a card offline.
    this.version(5).stores({
      giftCards: 'id, &code, version',
    });

    // Tenders are no longer a fixed list compiled into the app: the operator can add a
    // postal transfer or a cheque. The till needs them at the moment of payment, offline
    // included, so they replicate like the rest of the catalog.
    this.version(6).stores({
      paymentMethods: 'id, &key, version',
    });

    // Security review, 2026-10-10: a till no longer holds gift-card codes (it finds a card by the
    // code's hash) nor other branches' cashier PIN hashes. What it held before is dropped and the
    // next sync pulls everything again from the start, in the new shape.
    this.version(7)
      .stores({
        giftCards: 'id, codeHash, version',
      })
      .upgrade(async (tx) => {
        await tx.table('giftCards').clear();
        await tx.table('users').clear();
        await tx.table('meta').update('device', { cursor: 0 });
      });
  }
}

export const db = new PosDatabase();

export const DEFAULT_META: DeviceMeta = {
  key: 'device',
  // No terminal until someone signs in with one: a fresh browser is not "C01".
  deviceCode: '',
  cursor: 0,
  lastSyncAtUtc: null,
  lastSyncError: null,
  localSeq: 0,
  lang: 'fr',
  token: null,
  tokenExpiresAtUtc: null,
  currentUserId: null,
  apiBaseUrl: '',
};

export async function getMeta(): Promise<DeviceMeta> {
  const existing = await db.meta.get('device');
  if (existing) return existing;
  await db.meta.put(DEFAULT_META);
  return { ...DEFAULT_META };
}

export async function patchMeta(patch: Partial<DeviceMeta>): Promise<DeviceMeta> {
  const current = await getMeta();
  const next = { ...current, ...patch, key: 'device' as const };
  await db.meta.put(next);
  return next;
}

/** Wipes replicated data but keeps unsent sales — used when re-seeding from a full pull. */
export async function resetCatalog(): Promise<void> {
  await db.transaction(
    'rw',
    [db.products, db.categories, db.customers, db.users, db.stores, db.branches, db.giftCards, db.paymentMethods],
    async () => {
      await Promise.all([
        db.products.clear(),
        db.categories.clear(),
        db.customers.clear(),
        db.users.clear(),
        db.stores.clear(),
        db.branches.clear(),
        db.giftCards.clear(),
        db.paymentMethods.clear(),
      ]);
    },
  );
}
