import {
  AfterViewInit,
  Component,
  ElementRef,
  HostListener,
  OnDestroy,
  OnInit,
  ViewChild,
  computed,
  inject,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ConnectivityService } from '../../core/connectivity.service';
import { AuthService } from '../../core/auth.service';
import { AdminCustomer } from '../../core/admin.service';
import { canCreateCustomer, selectCreatedCustomer } from '../../core/customer-access';
import { QuickCustomerComponent } from '../backoffice/quick-customer.component';
import { I18nService } from '../../core/i18n.service';
import {
  IntegrationsService,
  MAX_CARD_REFERENCE_LENGTH,
  missingCardReference,
} from '../../core/integrations.service';
import { PaymentMethod, PaymentMethodDef, Product, Sale, soldAtTill } from '../../core/models';
import { cardCodeHash, maxRedeemValue, normalizeCardCode } from '../../core/loyalty';
import { TenderPart, productName, roundMoney, settleSplit } from '../../core/money';
import { PosService } from '../../core/pos.service';
import { SyncService } from '../../core/sync.service';
import { followSaleSync } from '../../core/follow-sale-sync';
import { listenForScans } from '../../core/barcode-scan';
import { matchCode, resolveEntry, searchProducts } from '../../core/product-search';
import { RegionalService } from '../../core/regional.service';
import { ConfirmService } from '../../ui/confirm.service';
import { DateInputComponent } from '../../ui/date-input.component';
import { removeLineRequest } from '../../ui/confirm-requests';
import { ReceiptComponent } from '../../shared/receipt.component';
import { SendReceiptComponent } from '../../shared/send-receipt.component';

@Component({
  selector: 'app-register',
  standalone: true,
  imports: [FormsModule, ReceiptComponent, SendReceiptComponent, QuickCustomerComponent, DateInputComponent],
  templateUrl: './register.component.html',
  styleUrl: './register.component.scss',
})
export class RegisterComponent implements OnInit, AfterViewInit, OnDestroy {
  readonly pos = inject(PosService);
  readonly i18n = inject(I18nService);
  readonly connectivity = inject(ConnectivityService);
  readonly regional = inject(RegionalService);
  readonly integrations = inject(IntegrationsService);
  private readonly confirm = inject(ConfirmService);

  @ViewChild('searchBox') searchBox?: ElementRef<HTMLInputElement>;
  @ViewChild('receivedBox') receivedBox?: ElementRef<HTMLInputElement>;

  private focusDiscType = false;
  private restoreTriggerFocus = false;

  @ViewChild('discTypeSelect') set discTypeSelect(el: ElementRef<HTMLSelectElement> | undefined) {
    if (el && this.focusDiscType) {
      this.focusDiscType = false;
      setTimeout(() => el.nativeElement.focus(), 0);
    }
  }

  @ViewChild('discountTriggerBtn') set discountTriggerBtn(el: ElementRef<HTMLButtonElement> | undefined) {
    if (el && this.restoreTriggerFocus) {
      this.restoreTriggerFocus = false;
      setTimeout(() => el.nativeElement.focus(), 0);
    }
  }

  readonly search = signal('');

  /**
   * How many product cards sit in a row. A terminal's own choice, not the company's: the
   * till by the door is a 10-inch tablet and the one in the office is a wide monitor, and
   * the same number of columns cannot be right for both. Kept on the device, so a cashier
   * sets it once and it survives the next login.
   */
  readonly columns = signal(readStoredColumns());
  readonly columnChoices = COLUMN_CHOICES;
  readonly toast = signal<string | null>(null);

  // ---------- FR-1: a new customer from the till ----------

  private readonly auth = inject(AuthService);

  /**
   * Only when the login says the server lets this user create a customer (`customers.create`),
   * and only online: there is no offline customer creation, so with no connection the button is
   * not drawn. Whoever lacks the capability keeps the walk-in customer.
   */
  readonly mayCreateCustomer = computed(() => canCreateCustomer(this.auth.currentUser(), this.connectivity.online()));
  readonly quickCustomer = signal(false);

  /**
   * Created on the server: pull it into this till's copy, then put it on the sale. The cart, the
   * discount and the tax choice are left exactly as they were.
   */
  async customerCreated(customer: AdminCustomer): Promise<void> {
    this.quickCustomer.set(false);
    const picked = await selectCreatedCustomer(
      customer.id,
      () => this.pos.refreshCatalog('customer'),
      (id) => this.pos.customers().some((c) => c.id === id),
      (id) => this.pos.customerId.set(id),
    );
    if (!picked) this.flash(`${customer.fullName} — ${this.i18n.t('register.customerNextSync')}`);
  }

  setColumns(n: number): void {
    if (!COLUMN_CHOICES.includes(n)) return;
    this.columns.set(n);
    try {
      localStorage.setItem(COLUMNS_KEY, String(n));
    } catch {
      /* private mode — the choice just won't survive the session */
    }
  }

  /**
   * False until the first read of the local catalog has come back. Without it the grid's
   * empty state fires on the very first frame and a cashier opening the till is told there
   * are no products, a moment before there are.
   */
  readonly catalogReady = signal(false);
  /** Whether a search term is narrowing the grid, which changes what empty means. */
  readonly filtering = computed(() => this.search().trim() !== '');

  /**
   * Whether the cart sheet is open. It only means anything below the till's two-column
   * breakpoint, where the cart docks to the bottom of the screen and its middle collapses;
   * on a wide terminal the cart is always open and the flag is ignored by the stylesheet.
   */
  readonly cartOpen = signal(false);
  /** Lines, not units: a quantity here can be 7.5 m³, which does not count as "7 articles". */
  readonly itemCount = computed(() => this.pos.items().length);

  readonly payOpen = signal(false);
  readonly payMethod = signal<PaymentMethod>('Cash');
  /** Which tender definition is selected. Distinguishes two operator-defined tenders. */
  readonly payMethodKey = signal<string>('cash');
  readonly received = signal<number>(0);
  readonly payError = signal<string | null>(null);
  readonly saving = signal(false);
  readonly completed = signal<Sale | null>(null);
  private readonly sync = inject(SyncService);
  /** The finished sale follows its sync, so the screen stops saying "saved offline" and the receipt can be sent. */
  private readonly followCompletedSync = followSaleSync(this.completed, (id) => this.pos.getSale(id), this.sync.pushed);

  discountType: 'none' | 'percent' | 'amount' = 'none';
  discountValue = 0;
  /** Presentation state: whether the discount editor row is open */
  readonly discountEditing = signal(false);

  /** Whether a discount is active in arithmetic */
  readonly hasDiscount = computed(() => {
    const d = this.pos.discount();
    return d.type !== 'none' && Number(d.value) > 0;
  });

  /** Presentation summary badge text (e.g. −10% or −500,00 DA) */
  readonly discountSummaryText = computed(() => {
    const d = this.pos.discount();
    if (d.type === 'percent') return `−${d.value}%`;
    if (d.type === 'amount') return `−${this.i18n.money(d.value)}`;
    return '';
  });

  /**
   * What the typed text can mean, best guess first.
   *
   * <p>`searchProducts` replaced five `includes()` calls that compared the raw strings. An
   * Arabic cashier typing سقاله — the same word written the way people write it, without the
   * vowel marks and with a plain ه — matched nothing against a catalogue that spells it
   * سقالة, and concluded the article was not in the system. The same fold finds
   * échafaudage typed without its accent.</p>
   */
  readonly matches = computed(() => searchProducts(this.saleProducts(), this.search(), Number.MAX_SAFE_INTEGER));

  /**
   * Every item is sold here as well as rented on contracts, from one stock (owner, 2026-10-06) —
   * except the workshop's raw materials, consumables and semi-finished goods (manufacturing, Phase 1).
   * One list for the grid, the search and the scanner, so no path offers what the others hide.
   */
  private readonly saleProducts = computed(() => this.pos.products().filter(soldAtTill));

  /**
   * The grid draws a page of results, not the whole catalog: a supplier's list runs to
   * hundreds and rendering all of them makes the first scroll stutter on a terminal.
   */
  readonly gridLimit = 120;
  readonly filtered = computed(() => this.matches().slice(0, this.gridLimit));
  /** Said out loud under the grid. A silently truncated list is a list a cashier cannot trust. */
  readonly truncated = computed(() => this.matches().length > this.gridLimit);

  /** Several methods on one sale (e.g. part cash, part card). Never rounded. */
  readonly splitMode = signal(false);
  readonly parts = signal<TenderPart[]>([]);
  readonly splitPaid = computed(() =>
    roundMoney(this.parts().reduce((sum, p) => sum + (Number(p.amount) || 0), 0), this.regional.decimals()),
  );
  readonly splitRemaining = computed(() =>
    roundMoney(Math.max(0, this.pos.totals().grandTotalTtc - this.splitPaid()), this.regional.decimals()),
  );

  /** Amount the customer owes with the chosen method: cash is rounded to the profile's step. */
  readonly due = computed(() =>
    !this.splitMode() && this.payMethod() === 'Cash' ? this.pos.cashDue() : this.pos.totals().grandTotalTtc,
  );
  readonly rounding = computed(() =>
    roundMoney(this.due() - this.pos.totals().grandTotalTtc, this.regional.decimals()),
  );
  readonly changeDue = computed(() =>
    this.splitMode()
      ? roundMoney(Math.max(0, this.splitPaid() - this.due()), this.regional.decimals())
      : roundMoney(Math.max(0, this.received() - this.due()), this.regional.decimals()),
  );

  /**
   * Tenders the operator enabled for this company, in the order they set.
   *
   * Taken from the replicated definitions so an operator-defined tender — a postal transfer,
   * a cheque — appears at the till like any other. A terminal that has not pulled since the
   * upgrade has an empty replica, so the country profile's list is synthesised into built-in
   * definitions instead: the payment screen must never come up blank.
   */
  readonly methods = computed<PaymentMethodDef[]>(() => {
    const enabled = this.regional.settings().paymentMethods;
    const replicated = this.pos
      .paymentMethods()
      .filter((m) => enabled.some((e) => e.toLowerCase() === m.key.toLowerCase()));
    if (replicated.length) return replicated;

    return (['Cash', 'Card', 'Credit', 'GiftCard', 'Loyalty'] as PaymentMethod[])
      .filter((m) => enabled.includes(m))
      .map((m, i) => builtInFallback(m, i));
  });

  /** Built-in tenders keep their shipped translation; an added one shows the operator's name. */
  methodLabel(def: PaymentMethodDef): string {
    if (def.isBuiltIn || def.behavior !== 'Generic') return this.i18n.t('pay.' + def.behavior.toLowerCase());
    const lang = this.i18n.lang();
    return (lang === 'ar' ? def.nameAr : lang === 'en' ? def.nameEn : def.nameFr) || def.nameFr || def.key;
  }

  /**
   * The built-in name a tender settles as. An operator-defined tender has none, so it rides
   * as Mixed and is told apart by its key — settlement treats it like any other non-cash
   * part, which is exactly right: money in, no side effect, no change given.
   */
  methodOf(def: PaymentMethodDef): PaymentMethod {
    return def.behavior === 'Generic' ? 'Mixed' : def.behavior;
  }

  defFor(key: string | undefined): PaymentMethodDef | undefined {
    return this.methods().find((m) => m.key === key);
  }

  /** Gift card code for a single-method GiftCard payment. */
  readonly cardCode = signal('');
  /** Bank terminal approval number, when the operator requires one for Card. */
  readonly cardReference = signal('');
  readonly maxReference = MAX_CARD_REFERENCE_LENGTH;
  readonly selectedCustomer = computed(() => this.pos.customers().find((c) => c.id === this.pos.customerId()) ?? null);
  /** What the selected customer's points can pay right now. */
  readonly loyaltyValue = computed(() =>
    maxRedeemValue(this.pos.loyalty(), this.selectedCustomer()?.loyaltyPoints ?? 0),
  );

  /** Local replica balance of a typed card code, or null when unknown. */
  cardBalance(code: string | null | undefined): number | null {
    const card = this.pos.giftCards().find((c) => c.codeHash === cardCodeHash(code));
    return card ? card.balance : null;
  }

  /** Quick-tender buttons: the exact total plus the country's usual notes above it. */
  readonly quickAmounts = computed(() => {
    const total = this.pos.cashDue();
    const notes = this.regional.settings().cashDenominations;
    const suggestions = new Set<number>([total]);
    for (const note of notes) {
      const rounded = Math.ceil(total / note) * note;
      if (rounded >= total) suggestions.add(rounded);
    }
    return [...suggestions].sort((a, b) => a - b).slice(0, 5);
  });

  async ngOnInit(): Promise<void> {
    try {
      await this.pos.loadCatalog();
    } finally {
      // Even a read that fails leaves the till usable, so the placeholder gives way either
      // way and the empty state below says what is actually there.
      this.catalogReady.set(true);
    }
  }

  /**
   * Escape closes the payment dialog, or starts the next sale once one is finished. A till is
   * worked from the keyboard between scans; reaching for the × is a movement away from it.
   */
  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (!this.payOpen()) return;
    if (this.completed()) this.newSale();
    else this.closePay();
  }

  clearSearch(): void {
    this.search.set('');
    this.focusSearch();
  }

  clearFilters(): void {
    this.search.set('');
    this.focusSearch();
  }

  /** "3 articles" reads wrong at one. The count is lines, so only the singular needs its own text. */
  readonly countLabel = computed(() =>
    this.itemCount() === 1
      ? this.i18n.t('register.itemsCountOne')
      : this.i18n.t('register.itemsCount', { n: this.itemCount() }),
  );

  /** The first quick-tender key is the amount owed exactly; it is labelled rather than guessed at. */
  isExact(amount: number): boolean {
    return amount === this.pos.cashDue();
  }

  ngAfterViewInit(): void {
    this.focusSearch();
  }

  /**
   * A scan made while the cashier was not in the search box.
   *
   * <p>Which is most of the time: they have just tapped a tile, changed a quantity, or come
   * back from another screen. Until now those scans typed the code into nothing at all, and
   * the cashier's recourse was to scan again, then reach for the mouse. The listener skips
   * anything typed into a field, so the search box keeps handling its own scans and nothing
   * is read twice.</p>
   */
  private readonly stopScanning = listenForScans((code) => this.onScan(code));

  ngOnDestroy(): void {
    this.stopScanning();
  }

  private async onScan(code: string): Promise<void> {
    // A scan during payment is the next customer's goods arriving early, or a gun knocked by
    // an elbow. Either way it does not belong in a basket that is being paid for.
    if (this.payOpen()) return;

    let found = matchCode(this.saleProducts(), code);
    if (!found.length) {
      await this.pullOnce();
      found = matchCode(this.saleProducts(), code);
    }
    if (found.length === 1) {
      this.addProduct(found[0]);
      return;
    }

    // Unknown, or on two articles at once. Put it where the cashier can see it rather than
    // deciding on their behalf.
    this.search.set(code);
    this.focusSearch();
    if (!found.length) this.flash(`${code} — ${this.i18n.t('register.notFound')}`);
  }

  name(product: Product): string {
    return productName(product, this.i18n.lang());
  }

  /**
   * The name to show for a cart line, in the language the till is being worked in.
   *
   * A line carries a name snapshot taken when it was added, and that snapshot is always the
   * French one (`pos.service.ts`), which is right for the receipt — a printed document should
   * not change wording later — and wrong on screen, where an Arabic cashier was reading a
   * French list under Arabic tiles. The catalog is the source while the product is still in
   * it; the snapshot stays the fallback for a line whose product has since gone.
   */
  lineName(item: { productId: string; name: string }): string {
    const product = this.pos.products().find((p) => p.id === item.productId);
    return product ? this.name(product) : item.name;
  }

  focusSearch(): void {
    setTimeout(() => this.searchBox?.nativeElement.focus(), 0);
  }

  /**
   * Barcode scanners behave as keyboards that end with Enter, so submitting the search box
   * is the scan handler: an exact barcode/SKU match is added straight to the cart.
   */
  async onSearchEnter(): Promise<void> {
    const term = this.search().trim();
    if (!term) return;

    const exact = this.pos.findByBarcode(term);
    if (exact) {
      this.addProduct(exact);
      this.search.set('');
      return;
    }

    const matches = this.filtered();
    if (matches.length === 1) {
      this.addProduct(matches[0]);
      this.search.set('');
      return;
    }
    // Several: the grid is showing them, and the cashier picks one.
    if (matches.length) return;

    // None at all — one look at the server before saying so.
    await this.pullOnce();
    const { taken } = resolveEntry(this.saleProducts(), term);
    if (taken) {
      this.addProduct(taken);
      this.search.set('');
      return;
    }
    this.flash(`${term} — ${this.i18n.t('register.notFound')}`);
  }

  /**
   * One incremental pull before a code is called unknown.
   *
   * <p>A code the till has never seen is most often an article added at the back office a
   * few minutes ago. The catalogue in memory follows the terminal's database, and that
   * follows the server once a minute — but a cashier with a box in hand should not wait for
   * the minute. The pull is a delta, so this is one small request; and a burst of unknown
   * scans shares one pull rather than queueing several.</p>
   */
  private pulling: Promise<unknown> | null = null;

  private pullOnce(): Promise<unknown> {
    this.pulling ??= this.pos.refreshCatalog('scan').finally(() => (this.pulling = null));
    return this.pulling;
  }

  addProduct(product: Product): void {
    if (product.trackStock && this.pos.availableStock(product) <= 0) {
      this.flash(`${this.name(product)} — ${this.i18n.t('register.outOfStock')}`);
      return;
    }
    this.pos.add(product, 1);
    this.focusSearch();
  }

  changeQty(productId: string, delta: number): void {
    const item = this.pos.items().find((i) => i.productId === productId);
    if (!item) return;
    this.pos.setQty(productId, item.qty + delta);
  }

  onQtyInput(productId: string, event: Event): void {
    const value = Number((event.target as HTMLInputElement).value);
    if (!Number.isNaN(value)) this.pos.setQty(productId, value);
  }

  applyDiscount(): void {
    this.pos.discount.set({ type: this.discountType, value: Number(this.discountValue) || 0 });
  }

  openDiscountEditor(): void {
    const current = this.pos.discount();
    this.discountType = current.type;
    this.discountValue = current.value;
    this.focusDiscType = true;
    this.discountEditing.set(true);
  }

  closeDiscountEditor(): void {
    this.restoreTriggerFocus = true;
    this.discountEditing.set(false);
  }

  removeDiscount(): void {
    this.discountType = 'none';
    this.discountValue = 0;
    this.applyDiscount();
    this.restoreTriggerFocus = true;
    this.discountEditing.set(false);
  }

  onCustomerChange(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.pos.customerId.set(value || null);
  }

  /**
   * Empties the cart, after asking when there is something in it: a whole basket rung up by
   * hand is a lot to lose to one tap. Asks nothing of the sale itself, which stays as it was.
   */
  async clearCart(): Promise<void> {
    const lines = this.pos.items().length;
    if (lines > 0) {
      const empty = await this.confirm.ask({
        title: this.i18n.t('register.clear.title'),
        message: this.i18n.t('register.clear.message', { n: lines }),
        confirmLabel: this.i18n.t('register.clear'),
        cancelLabel: this.i18n.t('confirm.keep'),
        danger: true,
      });
      if (!empty) return;
    }
    this.pos.clear();
    this.discountType = 'none';
    this.discountValue = 0;
    this.restoreTriggerFocus = false;
    this.discountEditing.set(false);
    this.focusSearch();
  }

  async removeLine(productId: string): Promise<void> {
    if (!(await this.confirm.ask(removeLineRequest(this.i18n)))) return;
    this.pos.remove(productId);
  }

  // ---------- payment ----------

  openPay(): void {
    if (!this.pos.items().length) return;
    const first = this.methods()[0];
    this.payMethod.set(first ? this.methodOf(first) : 'Cash');
    this.payMethodKey.set(first?.key ?? 'cash');
    this.splitMode.set(false);
    this.parts.set([]);
    this.cardCode.set('');
    this.cardReference.set('');
    this.received.set(this.due());
    this.payError.set(null);
    this.completed.set(null);
    this.payOpen.set(true);
    this.focusAmount();
  }

  /**
   * Puts the caret in the tendered-amount box with the amount owed selected, so the first
   * digit a cashier types replaces it. It is the next thing they do in every cash sale.
   */
  private focusAmount(): void {
    setTimeout(() => this.receivedBox?.nativeElement.select(), 0);
  }

  closePay(): void {
    this.payOpen.set(false);
    this.completed.set(null);
    // On a phone the cart sheet is left collapsed after a sale, so the next scan lands on a
    // full-height catalog rather than behind a list of lines that are already gone.
    this.cartOpen.set(false);
    this.discountEditing.set(false);
    this.focusSearch();
  }

  setMethod(def: PaymentMethodDef): void {
    this.payMethod.set(this.methodOf(def));
    this.payMethodKey.set(def.key);
    this.payError.set(null);
    this.cardReference.set('');
    this.received.set(this.due());
    this.focusAmount();
  }

  toggleSplit(): void {
    const on = !this.splitMode();
    this.splitMode.set(on);
    this.payError.set(null);
    const [first, second] = this.methods();
    const line = (def: PaymentMethodDef, amount: number): TenderPart =>
      ({ method: this.methodOf(def), methodKey: def.key, amount });
    // Start with the whole amount on the first method and an empty second line to fill in.
    this.parts.set(on && first ? [line(first, this.pos.totals().grandTotalTtc), line(second ?? first, 0)] : []);
  }

  addPart(): void {
    const first = this.methods()[0];
    if (!first) return;
    this.parts.set([
      ...this.parts(),
      { method: this.methodOf(first), methodKey: first.key, amount: this.splitRemaining() },
    ]);
  }

  /** The tender dropdown hands back a key; the settlement name comes from its definition. */
  setPartMethod(index: number, key: string): void {
    const def = this.defFor(key);
    if (!def) return;
    this.updatePart(index, { method: this.methodOf(def), methodKey: def.key, reference: null });
  }

  updatePart(index: number, patch: Partial<TenderPart>): void {
    this.parts.set(this.parts().map((p, i) => (i === index ? { ...p, ...patch, amount: Number(patch.amount ?? p.amount) || 0 } : p)));
    this.payError.set(null);
  }

  removePart(index: number): void {
    this.parts.set(this.parts().filter((_, i) => i !== index));
  }

  setReceived(value: number): void {
    this.received.set(Number(value) || 0);
  }

  onReceivedInput(event: Event): void {
    this.setReceived(Number((event.target as HTMLInputElement).value));
  }

  async confirmPayment(): Promise<void> {
    const totals = this.pos.totals();
    const method = this.payMethod();

    const split = this.splitMode();
    if (split) {
      const settled = settleSplit(this.parts(), totals.grandTotalTtc, this.regional.decimals());
      if ('problem' in settled) {
        this.payError.set(this.i18n.t('pay.split.' + settled.problem));
        return;
      }
    } else if (method === 'Cash' && this.received() + this.regional.moneyStep() / 2 < this.due()) {
      this.payError.set(this.i18n.t('pay.insufficient'));
      return;
    }
    const integrations = this.integrations.settings();
    const needsReference = split
      ? this.parts().some((p) => missingCardReference(integrations, p.method, p.amount, p.reference))
      : missingCardReference(integrations, method, this.due(), this.cardReference());
    if (needsReference) {
      this.payError.set(this.i18n.t('pay.cardReferenceRequired'));
      return;
    }

    const usesCredit = split ? this.parts().some((p) => p.method === 'Credit' && p.amount > 0) : method === 'Credit';
    if (usesCredit && !this.pos.customerId()) {
      this.payError.set(this.i18n.t('pay.creditNeedsCustomer'));
      return;
    }

    this.saving.set(true);
    try {
      const sale = await this.pos.checkout({
        method,
        methodKey: this.payMethodKey(),
        paidAmount: this.received(),
        parts: split ? this.parts() : undefined,
        reference: method === 'Card' ? this.cardReference().trim() : this.cardCode(),
      });
      this.completed.set(sale);
      this.discountType = 'none';
      this.discountValue = 0;
      this.discountEditing.set(false);
    } catch (error) {
      this.payError.set((error as Error)?.message ?? 'Error');
    } finally {
      this.saving.set(false);
    }
  }

  printReceipt(): void {
    window.print();
  }

  newSale(): void {
    this.closePay();
  }

  customerOf(sale: Sale) {
    return this.pos.customers().find((c) => c.id === sale.customerId) ?? null;
  }

  private flash(message: string): void {
    this.toast.set(message);
    setTimeout(() => this.toast.set(null), 2200);
  }
}

/**
 * A stand-in definition for a till that has not pulled since tenders became configurable.
 * It mirrors exactly what the five built-ins have always done, so the payment screen behaves
 * identically until the real definitions arrive on the next pull.
 */
function builtInFallback(method: PaymentMethod, index: number): PaymentMethodDef {
  return {
    id: method,
    key: method.toLowerCase(),
    nameAr: '',
    nameFr: '',
    nameEn: '',
    behavior: method === 'Mixed' ? 'Generic' : method,
    requiresConfirmation: false,
    affectsCashDrawer: method === 'Cash',
    requiresReference: method === 'GiftCard',
    requiresInstrumentDetails: false,
    isBuiltIn: true,
    isActive: true,
    sortOrder: index,
    version: 0,
    isDeleted: false,
  };
}

/** 2 to 5. Fewer than two wastes a wide screen; more than five makes a name unreadable. */
const COLUMN_CHOICES = [2, 3, 4, 5];
const COLUMNS_KEY = 'algeriapos.register.columns';

function readStoredColumns(): number {
  try {
    const stored = Number(localStorage.getItem(COLUMNS_KEY));
    if (COLUMN_CHOICES.includes(stored)) return stored;
  } catch {
    /* ignore */
  }
  // Four is what a 1440px till shows at the tile's natural width, so the default changes
  // nothing for a terminal whose cashier never touches the control.
  return 4;
}
