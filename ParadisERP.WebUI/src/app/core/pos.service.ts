import { Injectable, computed, effect, signal } from '@angular/core';
import { AuthService } from './auth.service';
import { db, getMeta, patchMeta } from './db';
import { paymentReference } from './integrations.service';
import {
  Category, Customer, GiftCard, PaymentMethod, PaymentMethodDef, Product, Sale, SaleLine, SalePaymentPart, Store, TaxMode, soldAtTill,
} from './models';
import { cardCodeHash, earnedPoints, loyaltyProgram, normalizeCardCode, pointsForAmount, tenderProblem } from './loyalty';
import { CartItem, CartTotals, TenderPart, buildReference, priceCart, round2, roundMoney, settleSplit } from './money';
import { RegionalService, roundCash } from './regional.service';
import { SyncService } from './sync.service';
import { canOperate } from './subscription-policy';
import { I18nService } from './i18n.service';
import { matchCode } from './product-search';

export interface DiscountState {
  type: 'none' | 'percent' | 'amount';
  value: number;
}

/** Cart state and sale persistence. Everything here works with no network. */
@Injectable({ providedIn: 'root' })
export class PosService {
  readonly products = signal<Product[]>([]);
  readonly categories = signal<Category[]>([]);
  readonly customers = signal<Customer[]>([]);
  readonly store = signal<Store | null>(null);
  readonly giftCards = signal<GiftCard[]>([]);
  /**
   * Tenders replicated from the server. The till needs these to draw the payment screen, so
   * they are read from the local replica and work with the line down.
   */
  readonly paymentMethods = signal<PaymentMethodDef[]>([]);
  readonly loyalty = computed(() => loyaltyProgram(this.store()));

  readonly items = signal<CartItem[]>([]);
  readonly discount = signal<DiscountState>({ type: 'none', value: 0 });
  readonly customerId = signal<string | null>(null);
  /**
   * Whether this document carries tax. Chosen per invoice because the customer may need a
   * taxed one or may not; prices are stored net, so an untaxed document is simply the net
   * with nothing added.
   */
  /**
   * Whether this sale carries tax. <b>Untaxed until the cashier says otherwise</b> — the owner's
   * decision (2026-09-25): prices are stored net, most counter sales go out without a tax line,
   * and a till that assumes tax puts one on a receipt nobody asked for. Each sale chooses; the
   * choice is on the pushed sale and on the receipt, so what was charged is never inferred later.
   */
  readonly taxMode = signal<TaxMode>('Untaxed');

  readonly totals = computed<CartTotals>(() => {
    const settings = this.regional.settings();
    return priceCart(this.items(), this.discount(), settings.pricesIncludeTax, settings.currencyDecimals, this.taxMode());
  });
  /** What a cash customer pays: the total rounded to the profile's cash step. */
  readonly cashDue = computed(() => roundCash(this.totals().grandTotalTtc, this.regional.settings()));
  readonly itemCount = computed(() =>
    this.items().reduce((sum, item) => sum + item.qty, 0),
  );

  constructor(
    private auth: AuthService,
    private sync: SyncService,
    private i18n: I18nService,
    private regional: RegionalService,
  ) {
    // A pull that changed the catalogue is read into memory straight away. The tiles and the
    // scan lookup work from these signals, not from IndexedDB, so without this a product
    // added at the back office sat in the terminal's database unseen until the next sale.
    effect(() => {
      if (this.sync.pulled() > 0) void this.loadCatalog();
    });

    // Read the local replica once at start-up. It is an IndexedDB read, not a request, and
    // without it a reload that lands straight in the back office leaves `store()` null — so the
    // company's chosen interface face (UiFontService reads it here) would not be applied until
    // the reader happened to open the register, the products, the sales or the sync screen.
    void this.loadCatalog();
  }

  private money(value: number): number {
    return roundMoney(value, this.regional.decimals());
  }

  async loadCatalog(): Promise<void> {
    const [products, categories, customers, stores, giftCards, paymentMethods] = await Promise.all([
      db.products.toArray(),
      db.categories.toArray(),
      db.customers.toArray(),
      db.stores.toArray(),
      db.giftCards.toArray(),
      db.paymentMethods.toArray(),
    ]);
    this.giftCards.set(giftCards);
    this.paymentMethods.set(
      paymentMethods.filter((m) => m.isActive && !m.isDeleted).sort((a, b) => a.sortOrder - b.sortOrder || a.key.localeCompare(b.key)),
    );

    this.products.set(products.filter((p) => p.isActive).sort((a, b) => a.sku.localeCompare(b.sku)));
    this.categories.set(categories.sort((a, b) => a.sortOrder - b.sortOrder));
    this.customers.set(customers.sort((a, b) => a.fullName.localeCompare(b.fullName)));
    this.store.set(stores[0] ?? null);
  }

  /**
   * Take a store row the back office has just saved straight into the local replica.
   *
   * The back office reads the same IndexedDB replica a till does — `store()` above is where
   * UiFontService, the receipt footer and the loyalty rules all come from — so a save that only
   * reached the server left the operator looking at the old company name, footer and interface
   * face on the very screen that changed them, until the sixty-second auto-sync or a click on
   * the connection button pulled the row back. The server already has it; this is the local
   * copy catching up in the same breath, which is what makes the change visible at once.
   *
   * Writing the row the server returned, rather than the form, is deliberate: it carries the
   * new `version`, so the next pull sees nothing to do instead of overwriting with a stale one.
   */
  async applyStore(store: Store): Promise<void> {
    // Memory first, so the screen shows what was saved even if the local write is slow — and
    // a local write that cannot land is never turned into an error about a save that worked:
    // the replica is one pull away from being right again either way.
    this.store.set(store);
    try {
      await db.stores.put(store);
    } catch {
      /* IndexedDB unavailable or full; the next pull writes the row. */
    }
  }

  /**
   * Ask the server for what changed and read it in: what a scan that matched nothing does
   * before the code is called unknown. The pull is a delta, so this is one small request and
   * not the catalogue again; `sync` already declines to run twice at once.
   */
  async refreshCatalog(reason: string): Promise<boolean> {
    const result = await this.sync.sync(reason);
    await this.loadCatalog();
    return result.ok;
  }

  // ---------- cart ----------

  add(product: Product, qty = 1): void {
    const items = [...this.items()];
    const index = items.findIndex((i) => i.productId === product.id);
    if (index >= 0) {
      items[index] = { ...items[index], qty: round2(items[index].qty + qty) };
    } else {
      items.push({
        productId: product.id,
        sku: product.sku,
        name: product.nameFr || product.nameEn || product.nameAr,
        qty,
        unitPrice: product.price,
        taxRate: product.taxRate,
        unit: product.unit,
      });
    }
    this.items.set(items.filter((i) => i.qty > 0));
  }

  setQty(productId: string, qty: number): void {
    const items = this.items()
      .map((i) => (i.productId === productId ? { ...i, qty: round2(qty) } : i))
      .filter((i) => i.qty > 0);
    this.items.set(items);
  }

  setUnitPrice(productId: string, price: number): void {
    this.items.set(
      this.items().map((i) =>
        i.productId === productId ? { ...i, unitPrice: Math.max(0, this.money(price)) } : i,
      ),
    );
  }

  remove(productId: string): void {
    this.items.set(this.items().filter((i) => i.productId !== productId));
  }

  clear(): void {
    this.items.set([]);
    this.discount.set({ type: 'none', value: 0 });
    this.customerId.set(null);
  }

  /**
   * The article a scanner just read.
   *
   * <p>Delegated to `matchCode` so the till agrees with the back office about what counts as
   * the same code — the spacing, the case, and the leading zero that tells a 12-digit UPC
   * from the 13-digit EAN of the same box. It used to compare the barcode byte for byte,
   * which meant the same article scanned differently on two guns in one shop.</p>
   *
   * <p>Nothing when two articles carry the code: guessing between them would put the wrong
   * one in the basket every time and never say so.</p>
   */
  findByBarcode(code: string): Product | undefined {
    // Every item is sold at the till as well as rented (owner, 2026-10-06) — except the workshop's
    // materials (manufacturing, Phase 1), which a scan at the counter does not find.
    const found = matchCode(this.products().filter(soldAtTill), code);
    return found.length === 1 ? found[0] : undefined;
  }

  /** Stock the customer can still buy: server figure minus what is already in the cart. */
  availableStock(product: Product): number {
    const inCart = this.items().find((i) => i.productId === product.id)?.qty ?? 0;
    return round2(product.stockQty - inCart);
  }

  // ---------- checkout ----------

  /**
   * Writes the sale to IndexedDB and queues it for upload. The local write is what
   * completes the sale — the network attempt afterwards is best-effort, so the register
   * never waits on it and never loses a sale to a dropped connection.
   */
  async checkout(input: {
    method: PaymentMethod;
    /** Tender definition key for the single-method path; defaults to the built-in name. */
    methodKey?: string;
    paidAmount: number;
    /** Two or more methods on one sale; the sale is then recorded as Mixed. */
    parts?: TenderPart[];
    /** Gift card code when the single method is GiftCard. */
    reference?: string | null;
    note?: string | null;
  }): Promise<Sale> {
    const user = this.auth.currentUser();
    if (!user) throw new Error('Not signed in.');
    if (!this.items().length) throw new Error('Cart is empty.');

    const totals = this.totals();
    const split = input.parts ? settleSplit(input.parts, totals.grandTotalTtc, this.regional.decimals()) : null;
    if (split && 'problem' in split) throw new Error(this.i18n.t('pay.split.' + split.problem));

    // Reserve the receipt number atomically so two fast sales can't collide.
    const meta = await getMeta();
    if (!canOperate(meta.subscription)) throw new Error(this.i18n.t('subscription.inactive'));
    const localSeq = meta.localSeq + 1;
    await patchMeta({ localSeq });

    // Only an all-cash sale is rounded; the difference is kept apart so the tax figures stay exact.
    const method: PaymentMethod = split ? 'Mixed' : input.method;
    const due = method === 'Cash' ? this.cashDue() : totals.grandTotalTtc;
    const roundingAdjustment = this.money(due - totals.grandTotalTtc);
    const paid = split ? split.paid : method === 'Credit' ? 0 : this.money(input.paidAmount);
    const change = split ? split.change : method === 'Cash' ? this.money(Math.max(0, paid - due)) : 0;
    const payments: SalePaymentPart[] = split
      ? split.parts.map((p) => ({
          id: crypto.randomUUID(),
          method: p.method,
          methodKey: p.methodKey ?? p.method,
          amount: p.amount,
          reference: paymentReference(p.method, p.reference),
          instrumentNumber: p.instrumentNumber ?? null,
          instrumentBank: p.instrumentBank ?? null,
          instrumentDueDate: p.instrumentDueDate ?? null,
        }))
      : [
          {
            id: crypto.randomUUID(),
            method,
            methodKey: input.methodKey ?? method,
            // What the customer actually handed over, not what the drawer kept. The server
            // subtracts the change itself — netting it here too took it off twice, so every
            // cash sale under-stated the drawer by exactly the change given back.
            amount: method === 'Cash' || method === 'Card' ? this.money(paid) : totals.grandTotalTtc,
            reference: paymentReference(method, input.reference),
          },
        ];
    const customer = this.customers().find((c) => c.id === this.customerId()) ?? null;
    const problem = tenderProblem(payments, this.giftCards(), customer, this.loyalty());
    if (problem) {
      await patchMeta({ localSeq: meta.localSeq });
      throw new Error(this.i18n.t('pay.tender.' + problem));
    }

    const sale: Sale = {
      id: crypto.randomUUID(),
      reference: buildReference(meta.deviceCode, localSeq),
      localSeq,
      soldAtUtc: new Date().toISOString(),
      customerId: this.customerId(),
      cashierUserId: user.id,
      cashierName: user.fullName || user.username,
      subTotalHt: totals.subTotalHt,
      taxTotal: totals.taxTotal,
      discountTotal: totals.discountTotal,
      grandTotalTtc: totals.grandTotalTtc,
      roundingAdjustment,
      paidAmount: paid,
      changeAmount: change,
      paymentMethod: method,
      taxMode: this.taxMode(),
      status: 'Completed',
      refundOfSaleId: null,
      note: input.note ?? null,
      lines: totals.lines,
      payments,
      outboxState: 'pending',
      attempts: 0,
      lastError: null,
      syncedAtUtc: null,
    };

    await db.transaction('rw', [db.sales, db.products, db.giftCards, db.customers], async () => {
      await db.sales.add(sale);
      await this.applyRewards(sale, -1);

      // Optimistic local stock so the next customer sees a realistic figure. The server
      // recomputes the true number and overwrites this on the next pull.
      for (const line of sale.lines) {
        const product = await db.products.get(line.productId);
        if (product?.trackStock) {
          await db.products.update(product.id, { stockQty: round2(product.stockQty - line.qty) });
        }
      }
    });

    this.clear();
    this.taxMode.set('Untaxed');
    await this.loadCatalog();

    // Fire and forget: a failure here is normal and simply leaves the sale in the outbox.
    void this.sync.sync('after-sale');

    return sale;
  }

  /**
   * Optimistic gift card and loyalty balances, mirroring the server's LoyaltyLedger; the next
   * pull replaces them with the server figures. direction -1 = sale, +1 = refund.
   */
  private async applyRewards(sale: Sale, direction: 1 | -1): Promise<void> {
    for (const p of sale.payments.filter((x) => x.method === 'GiftCard' && x.reference)) {
      const card = await db.giftCards.where('codeHash').equals(cardCodeHash(p.reference)).first();
      if (card) await db.giftCards.update(card.id, { balance: this.money(card.balance + direction * p.amount) });
    }
    if (!sale.customerId) return;
    const customer = await db.customers.get(sale.customerId);
    if (!customer) return;
    const program = this.loyalty();
    const redeemed = pointsForAmount(program, sale.payments.filter((p) => p.method === 'Loyalty').reduce((s, p) => s + p.amount, 0));
    const earned = earnedPoints(program, sale.grandTotalTtc, sale.payments, direction === 1);
    const delta = direction * redeemed - direction * earned;
    if (delta) await db.customers.update(customer.id, { loyaltyPoints: (customer.loyaltyPoints ?? 0) + delta });
  }

  async recentSales(limit = 100, pendingOnly = false): Promise<Sale[]> {
    const all = await db.sales.orderBy('soldAtUtc').reverse().limit(limit * 2).toArray();
    const filtered = pendingOnly
      ? all.filter((s) => s.outboxState !== 'synced')
      : all;
    return filtered.slice(0, limit);
  }

  getSale(id: string): Promise<Sale | undefined> {
    return db.sales.get(id);
  }

  // ---------- refunds ----------

  /**
   * A sale that already has a refund recorded against it locally. Kept simple on purpose:
   * one refund per sale, full or partial — re-refunding the remainder is not supported, so
   * a cashier who under-refunds has to be corrected by a manager off-system.
   */
  async refundedSaleIds(): Promise<Set<string>> {
    const all = await db.sales.toArray();
    return new Set(all.filter((s) => s.refundOfSaleId).map((s) => s.refundOfSaleId!));
  }

  /**
   * Writes a refund as its own append-only sale with Status = 'Refunded', exactly like a
   * normal checkout. The server tells sales and refunds apart purely by Status — never by a
   * negative amount — so every figure here stays positive; PersistSaleAsync on the server
   * adds the stock back and, when the refund method is Credit, reduces the customer's
   * balance instead of increasing it.
   */
  async refund(
    original: Sale,
    selections: { lineId: string; qty: number }[],
    method: PaymentMethod,
    reference: string | null = null,
  ): Promise<Sale> {
    const user = this.auth.currentUser();
    if (!user) throw new Error('Not signed in.');

    const lines: SaleLine[] = [];
    for (const sel of selections) {
      if (sel.qty <= 0) continue;
      const orig = original.lines.find((l) => l.id === sel.lineId);
      if (!orig || orig.qty <= 0) continue;
      const qty = Math.min(round2(sel.qty), orig.qty);
      const ratio = qty / orig.qty;
      lines.push({
        id: crypto.randomUUID(),
        productId: orig.productId,
        sku: orig.sku,
        nameSnapshot: orig.nameSnapshot,
        qty,
        unitPrice: orig.unitPrice,
        taxRate: orig.taxRate,
        lineDiscount: this.money(orig.lineDiscount * ratio),
        lineTotalTtc: this.money(orig.lineTotalTtc * ratio),
        lineTaxAmount: this.money(orig.lineTaxAmount * ratio),
      });
    }
    if (!lines.length) throw new Error('Select at least one item to refund.');

    const grandTotalTtc = this.money(lines.reduce((sum, l) => sum + l.lineTotalTtc, 0));
    const taxTotal = this.money(lines.reduce((sum, l) => sum + l.lineTaxAmount, 0));
    const discountTotal = this.money(lines.reduce((sum, l) => sum + l.lineDiscount, 0));
    const subTotalHt = this.money(grandTotalTtc - taxTotal);

    const meta = await getMeta();
    if (!canOperate(meta.subscription)) throw new Error(this.i18n.t('subscription.inactive'));
    const localSeq = meta.localSeq + 1;
    await patchMeta({ localSeq });

    const sale: Sale = {
      id: crypto.randomUUID(),
      reference: buildReference(meta.deviceCode, localSeq),
      localSeq,
      soldAtUtc: new Date().toISOString(),
      customerId: original.customerId,
      cashierUserId: user.id,
      cashierName: user.fullName || user.username,
      subTotalHt,
      taxTotal,
      discountTotal,
      grandTotalTtc,
      paidAmount: grandTotalTtc,
      changeAmount: 0,
      paymentMethod: method,
      status: 'Refunded',
      refundOfSaleId: original.id,
      note: null,
      lines,
      payments: [{ id: crypto.randomUUID(), method, amount: grandTotalTtc, reference: method === 'GiftCard' ? normalizeCardCode(reference) : null }],
      outboxState: 'pending',
      attempts: 0,
      lastError: null,
      syncedAtUtc: null,
    };

    if (method === 'GiftCard' && !(await db.giftCards.where('codeHash').equals(cardCodeHash(reference)).first()))
      throw new Error(this.i18n.t('pay.tender.giftCardUnknown'));
    await db.transaction('rw', [db.sales, db.products, db.giftCards, db.customers], async () => {
      await db.sales.add(sale);
      await this.applyRewards(sale, 1);

      // Optimistic local stock: the refund hands the item back, so add it here and let the
      // next pull replace the estimate with the server's authoritative figure.
      for (const line of sale.lines) {
        const product = await db.products.get(line.productId);
        if (product?.trackStock) {
          await db.products.update(product.id, { stockQty: round2(product.stockQty + line.qty) });
        }
      }
    });

    await this.loadCatalog();
    void this.sync.sync('after-refund');

    return sale;
  }
}
