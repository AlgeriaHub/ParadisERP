import { AdminProduct } from './admin.service';
import { ProductCatalog } from './catalog-store';

const row = (id: string, isActive = true): AdminProduct => ({
  id,
  sku: id.toUpperCase(),
  barcode: null,
  nameAr: '',
  nameFr: id,
  nameEn: '',
  categoryId: null,
  price: 0,
  costPrice: 0,
  taxRate: 0,
  unit: 'pcs',
  trackStock: true,
  isActive,
  isRentable: false,
  assetUnitValue: 0,
  version: 1,
});

/** A server that counts how often it is asked and answers with whatever it holds now. */
function server(rows: AdminProduct[]) {
  const state = { rows, calls: 0 };
  return {
    state,
    fetchAll: () => {
      state.calls++;
      return Promise.resolve(state.rows);
    },
  };
}

/** A server that does not answer until told to, so two callers can be caught mid-flight. */
function heldServer(rows: AdminProduct[]) {
  let release!: () => void;
  const gate = new Promise<void>((resolve) => (release = resolve));
  const state = { calls: 0 };
  return {
    state,
    release: () => release(),
    fetchAll: async () => {
      state.calls++;
      await gate;
      return rows;
    },
  };
}

describe('ProductCatalog', () => {
  it('fetches once a session and serves everyone after that from memory', async () => {
    const api = server([row('a'), row('b')]);
    const catalog = new ProductCatalog(api.fetchAll);

    await catalog.ensure();
    await catalog.ensure();
    await catalog.ensure();

    expect(api.state.calls).toBe(1);
    expect(catalog.products().map((p) => p.id)).toEqual(['a', 'b']);
  });

  it('answers ten lookups with no request at all', async () => {
    // The owner's exact concern: a contract with ten articles must not be ten trips.
    const api = server([row('a')]);
    const catalog = new ProductCatalog(api.fetchAll);
    await catalog.ensure();

    for (let i = 0; i < 10; i++) catalog.products().find((p) => p.id === 'a');

    expect(api.state.calls).toBe(1);
  });

  it('refresh asks again and replaces the list', async () => {
    const api = server([row('a')]);
    const catalog = new ProductCatalog(api.fetchAll);
    await catalog.ensure();

    api.state.rows = [row('a'), row('new')];
    await catalog.refresh();

    expect(api.state.calls).toBe(2);
    expect(catalog.products().map((p) => p.id)).toEqual(['a', 'new']);
  });

  it('shares one fetch between callers who arrive while it is in the air', async () => {
    // Two pickers missing the same scan at the same moment is one request.
    const api = heldServer([row('a')]);
    const catalog = new ProductCatalog(api.fetchAll);

    const first = catalog.refresh();
    const second = catalog.refresh();
    api.release();

    expect(await first).toBe(await second);
    expect(api.state.calls).toBe(1);
  });

  it('invalidate makes the next ensure fetch, and fetches nothing before then', async () => {
    const api = server([row('a')]);
    const catalog = new ProductCatalog(api.fetchAll);
    await catalog.ensure();

    catalog.invalidate();
    expect(api.state.calls).toBe(1);

    await catalog.ensure();
    expect(api.state.calls).toBe(2);
  });

  it('keeps the previous list when a fetch fails, and reports the failure', async () => {
    // A screen with yesterday's list still works; a screen with no list does not.
    const api = server([row('a')]);
    const catalog = new ProductCatalog(api.fetchAll);
    await catalog.ensure();

    api.fetchAll = () => Promise.reject(new Error('down'));
    const failing = new ProductCatalog(api.fetchAll);
    failing.products.set(catalog.products());
    failing.loaded.set(true);

    await expectAsync(failing.refresh()).toBeRejectedWithError('down');
    expect(failing.products().map((p) => p.id)).toEqual(['a']);
    expect(failing.loaded()).toBeTrue();
  });

  it('offers a document only the active articles', async () => {
    const api = server([row('a'), row('gone', false)]);
    const catalog = new ProductCatalog(api.fetchAll);
    await catalog.ensure();

    expect(catalog.active().map((p) => p.id)).toEqual(['a']);
    expect(catalog.products().length).toBe(2);
  });
});
