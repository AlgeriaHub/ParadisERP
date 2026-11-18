import { Component, OnInit, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { AdminBranch, AdminCustomer, AdminPaymentMethod, AdminService, adminErrorMessage } from '../../../core/admin.service';
import { AuthService } from '../../../core/auth.service';
import { canOpen } from '../../../core/nav';
import { PageChrome } from '../shell/page-chrome';
import { CatalogStore } from '../../../core/catalog-store';
import { I18nService } from '../../../core/i18n.service';
import { PrintKind, PrintService } from '../../print/print.service';
import { AccountPart } from './receivable-view';
import { CreateInvoiceResponse, CreditNote, ReceiptVoucher } from './receivables-api.service';
import { ReceivableHomeComponent } from './receivable-home.component';
import { ReceivableAccountComponent } from './receivable-account.component';
import { ReceivableInvoicesComponent } from './receivable-invoices.component';
import { ReceivableInvoiceComponent } from './receivable-invoice.component';
import { ReceivableInvoiceFormComponent } from './receivable-invoice-form.component';
import { ReceivableCreditNoteFormComponent } from './receivable-credit-note-form.component';
import { ReceivableReceiptsComponent } from './receivable-receipts.component';
import { ReceivableReceiptComponent } from './receivable-receipt.component';
import { ReceivableReceiptFormComponent } from './receivable-receipt-form.component';

/** The three doors into the debtor book; a document opened from a list keeps that list's chip lit. */
type Section = 'home' | 'invoices' | 'receipts';

/**
 * Where the section is. A customer's account is the centre of it: documents opened from an
 * account carry `customerId` so their back button lands on the account, not on a list.
 */
type View =
  | { kind: 'home' }
  | { kind: 'invoices' }
  | { kind: 'receipts' }
  | { kind: 'account'; customerId: string; part: AccountPart; done?: string | null }
  | { kind: 'invoice'; id: string; customerId: string; done?: string | null; warning?: string | null }
  | { kind: 'newInvoice'; customerId: string | null }
  | { kind: 'creditNote'; invoiceId: string; customerId: string }
  | { kind: 'receipt'; id: string; customerId: string; done?: string | null; outcome?: 'Bounced' | null }
  | { kind: 'newReceipt'; customerId: string | null; invoiceId: string | null };

/** Tenders that move a balance of their own cannot settle a customer account; the server refuses them. */
const NOT_A_RECEIPT_TENDER = new Set<AdminPaymentMethod['behavior']>(['Credit', 'GiftCard', 'Loyalty']);

/**
 * Customer accounts: who owes what, since when, and what has actually been collected.
 *
 * <p>The section lands on the debtors ranked by lateness, because that is the question the
 * owner opens it for. One click opens a customer's account, and every document — invoice,
 * credit note, receipt — lives under that head. Raising one is a task that replaces the screen,
 * and starts with the customer already fixed when it was started from an account, so nobody is
 * asked the same thing twice.</p>
 *
 * <p>Customers, branches, tenders and the catalogue are loaded once here and handed down;
 * eleven screens each fetching the same four is what makes a section feel slow. The composed
 * "done" sentences are written here too, so a detail screen only has to show what it is given.
 * The credit-limit warning is passed raw: the invoice screen localises it from its figures.</p>
 */
@Component({
  selector: 'app-backoffice-receivables',
  standalone: true,
  imports: [
    ReceivableHomeComponent,
    ReceivableAccountComponent,
    ReceivableInvoicesComponent,
    ReceivableInvoiceComponent,
    ReceivableInvoiceFormComponent,
    ReceivableCreditNoteFormComponent,
    ReceivableReceiptsComponent,
    ReceivableReceiptComponent,
    ReceivableReceiptFormComponent,
  ],
  template: `
    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      <!-- What was just saved, with the paper offered next to it. A receipt and a credit note
           are documents the customer leaves with, so the moment they exist is the moment they
           are printed; sending the reader hunting for the row in a table afterwards is how
           vouchers end up unprinted. -->
      @if (justSaved(); as p) {
        <p class="notice ok" role="status">
          {{ p.label }}
          <button type="button" class="btn-ghost bo-small" (click)="printer.open(p.kind, p.id)">
            {{ i18n.t('common.print') }}
          </button>
        </p>
      }

      @if (!page()) {
        <nav class="bo-chips" [attr.aria-label]="i18n.t('backoffice.tabs.receivables')">
          @for (s of SECTIONS; track s) {
            <button
              type="button"
              [class.is-on]="section() === s"
              [attr.aria-current]="section() === s ? 'true' : null"
              (click)="show(s)"
            >
              {{ i18n.t('inv.section.' + s) }}
            </button>
          }
        </nav>
      }

      @if (!loaded()) {
        <p class="bo-loading">
          <span class="bo-loading__word">{{ i18n.t('common.loading') }}</span>
          <span class="bo-loading__rows" aria-hidden="true"></span>
        </p>
      } @else {
        @switch (view().kind) {
          @case ('home') {
            <app-receivable-home
              [customers]="customers()"
              (openAccount)="go({ kind: 'account', customerId: $event, part: 'statement' })"
              (newInvoice)="go({ kind: 'newInvoice', customerId: null })"
              (newReceipt)="go({ kind: 'newReceipt', customerId: null, invoiceId: null })"
            />
          }
          @case ('invoices') {
            <app-receivable-invoices
              [customers]="customers()"
              (openInvoice)="go({ kind: 'invoice', id: $event.id, customerId: $event.customerId })"
              (openAccount)="go({ kind: 'account', customerId: $event, part: 'invoices' })"
              (newInvoice)="go({ kind: 'newInvoice', customerId: $event })"
              (collect)="go({ kind: 'newReceipt', customerId: $event.customerId, invoiceId: $event.invoiceId })"
            />
          }
          @case ('receipts') {
            <app-receivable-receipts
              [customers]="customers()"
              [methods]="methods()"
              [branches]="branches()"
              [showAccount]="mayOpenAccounts()"
              [linkBase]="addressed() ? RECEIPTS_URL : null"
              (openReceipt)="go({ kind: 'receipt', id: $event.id, customerId: $event.customerId })"
              (openAccount)="openAccount($event, 'receipts')"
              (newReceipt)="go({ kind: 'newReceipt', customerId: $event, invoiceId: null })"
              (bounce)="go({ kind: 'receipt', id: $event.id, customerId: $event.customerId, outcome: 'Bounced' })"
            />
          }
          @case ('account') {
            @if (account(); as a) {
              <app-receivable-account
                [customerId]="a.customerId"
                [part]="a.part"
                [customers]="customers()"
                [branches]="branches()"
                [methods]="methods()"
                [done]="a.done ?? null"
                (back)="go({ kind: 'home' })"
                (partChanged)="go({ kind: 'account', customerId: a.customerId, part: $event })"
                (newInvoice)="go({ kind: 'newInvoice', customerId: $event })"
                (newReceipt)="go({ kind: 'newReceipt', customerId: $event.customerId, invoiceId: $event.invoiceId })"
                (openInvoice)="go({ kind: 'invoice', id: $event.id, customerId: $event.customerId })"
                (openReceipt)="go({ kind: 'receipt', id: $event.id, customerId: $event.customerId })"
                (bounce)="go({ kind: 'receipt', id: $event.id, customerId: $event.customerId, outcome: 'Bounced' })"
              />
            }
          }
          @case ('invoice') {
            @if (invoice(); as v) {
              <app-receivable-invoice
                [id]="v.id"
                [customerId]="v.customerId"
                [customers]="customers()"
                [branches]="branches()"
                [methods]="methods()"
                [done]="v.done ?? null"
                [warning]="v.warning ?? null"
                (back)="go({ kind: 'account', customerId: $event, part: 'invoices' })"
                (creditNote)="go({ kind: 'creditNote', invoiceId: $event.id, customerId: $event.customerId })"
                (collect)="go({ kind: 'newReceipt', customerId: $event.customerId, invoiceId: $event.invoiceId })"
                (openReceipt)="go({ kind: 'receipt', id: $event.id, customerId: $event.customerId })"
                (openAccount)="go({ kind: 'account', customerId: $event, part: 'statement' })"
              />
            }
          }
          @case ('newInvoice') {
            @if (newInvoice(); as v) {
              <app-receivable-invoice-form
                [customerId]="v.customerId"
                [customers]="customers()"
                [branches]="branches()"
                (saved)="invoiceSaved($event)"
                (cancelled)="invoiceCancelled($event)"
              />
            }
          }
          @case ('creditNote') {
            @if (creditNote(); as v) {
              <app-receivable-credit-note-form
                [invoiceId]="v.invoiceId"
                [customerId]="v.customerId"
                [customers]="customers()"
                (saved)="creditNoteSaved($event, v.customerId)"
                (cancelled)="go({ kind: 'invoice', id: $event.id, customerId: $event.customerId })"
              />
            }
          }
          @case ('receipt') {
            @if (receipt(); as v) {
              <!-- Keyed on the receipt and the act: another address is another screen, loaded afresh. -->
              @for (key of [v.id + ':' + (v.outcome ?? '')]; track key) {
                <app-receivable-receipt
                  [id]="v.id"
                  [customerId]="v.customerId"
                  [customers]="customers()"
                  [branches]="branches()"
                  [methods]="methods()"
                  [done]="v.done ?? null"
                  [outcome]="v.outcome ?? null"
                  [addressed]="addressed()"
                  [showAccount]="mayOpenAccounts()"
                  (back)="go({ kind: 'account', customerId: $event, part: 'receipts' })"
                  (openInvoice)="go({ kind: 'invoice', id: $event.id, customerId: $event.customerId })"
                  (openAccount)="openAccount($event, 'statement')"
                />
              }
            }
          }
          @case ('newReceipt') {
            @if (newReceipt(); as v) {
              <app-receivable-receipt-form
                [customerId]="v.customerId"
                [invoiceId]="v.invoiceId"
                [customers]="customers()"
                [branches]="branches()"
                [methods]="methods()"
                (saved)="receiptSaved($event)"
                (cancelled)="receiptCancelled($event)"
              />
            }
          }
        }
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
  styles: [
    `
      /* The offer belongs in the sentence rather than under it, and the gap is logical so the
         Arabic layout puts it on the other side of the words with no second rule. */
      .notice > .btn-ghost {
        margin-inline-start: 0.5rem;
      }
    `,
  ],
})
export class BackofficeReceivablesComponent implements OnInit {
  private readonly admin = inject(AdminService);
  /** The session's one copy of the catalogue; the invoice form reads it for product lines. */
  private readonly catalog = inject(CatalogStore);
  readonly printer = inject(PrintService);
  readonly i18n = inject(I18nService);

  /** A customer to open straight away — the customers tab's "account" button lands here. */
  readonly start = input<string | null>(null);
  /** Bumped by the rail when its entry is clicked while this section is already open: land on the board. */
  readonly reset = input(0);
  /**
   * A cashier who holds the cash box (answer 25): the receipts only. The debtor book, the invoices
   * and the statements are the manager's, and the server refuses them to him anyway.
   */
  readonly receiptsOnly = input(false);

  readonly SECTIONS_ALL: readonly Section[] = ['home', 'invoices', 'receipts'];
  get SECTIONS(): readonly Section[] {
    return this.receiptsOnly() ? ['receipts'] : this.SECTIONS_ALL;
  }
  /** Set only by the chips: a document opened from a list keeps that list's chip lit. */
  /**
   * The chip the route opened this section on (core/nav.ts). Set, the sidebar is the navigation
   * and this component's own chip strip is not drawn; unset, it behaves as it always did.
   */
  readonly page = input<Section | null>(null);
  /**
   * The address under `finance/receipts` (core/nav.ts `deep`): `[]` the list, `['new']`, `[id]` a
   * receipt. Only the receipts page is addressed so far; the debtor book and the invoices become
   * addresses with Customers & accounts (Phase 4, step 3) and keep their views in memory until then.
   */
  readonly path = input<string[]>([]);
  readonly addressed = computed(() => this.page() === 'receipts');
  readonly RECEIPTS_URL = RECEIPTS_URL;
  readonly section = signal<Section>('home');
  readonly view = signal<View>({ kind: 'home' });
  /**
   * The document just saved: its sentence and the kind the print route draws it as. Held here
   * rather than handed to the detail screen as `done`, because the sentence and the button that
   * prints what it names have to stay together, and both stop being true the moment the reader
   * navigates — which is why `go` clears it.
   */
  readonly justSaved = signal<{ kind: PrintKind; id: string; label: string } | null>(null);
  readonly error = signal<string | null>(null);
  readonly loaded = signal(false);

  readonly customers = signal<AdminCustomer[]>([]);
  readonly branches = signal<AdminBranch[]>([]);
  readonly methods = signal<AdminPaymentMethod[]>([]);

  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly auth = inject(AuthService);
  private readonly chrome = inject(PageChrome, { optional: true });
  private readonly query = toSignal(this.route.queryParamMap, { initialValue: this.route.snapshot.queryParamMap });

  /** The account is the Customers module's page: offered only to whoever the nav table lets open it. */
  readonly mayOpenAccounts = computed(() => canOpen(this.auth.currentUser(), 'customers.accounts'));

  /** The view the address names, on the receipts page; null elsewhere. */
  private readonly fromAddress = computed<View | null>(() => {
    if (!this.addressed()) return null;
    const q = this.query();
    return receiptViewFor(this.path(), { act: q.get('act'), customer: q.get('customer'), invoice: q.get('invoice') });
  });

  // Narrowed views for the template: `@switch` does not narrow the union by itself, and a
  // bound `view().customerId` on the wrong variant is exactly what the AOT build refuses.
  readonly account = () => this.pick('account');
  readonly invoice = () => this.pick('invoice');
  readonly newInvoice = () => this.pick('newInvoice');
  readonly creditNote = () => this.pick('creditNote');
  readonly receipt = () => this.pick('receipt');
  readonly newReceipt = () => this.pick('newReceipt');

  private pick<K extends View['kind']>(kind: K): Extract<View, { kind: K }> | null {
    const view = this.view();
    return view.kind === kind ? (view as Extract<View, { kind: K }>) : null;
  }

  private seenReset = 0;

  constructor() {
    // On the receipts page the address decides the view: a reload, a deep link, Back and Forward.
    effect(() => {
      const view = this.fromAddress();
      if (!view) return;
      untracked(() => {
        this.view.set(view);
        if (view.kind === 'receipts') this.setHeading(null);
        else if (view.kind === 'newReceipt') this.setHeading(this.i18n.t('inv.acct.newReceipt'));
        // A receipt names itself in the header once it has loaded (receivable-receipt.component.ts).
      });
    });

    // The first run only records the value it was created with; a later change is a click.
    effect(() => {
      const n = this.reset();
      if (n === this.seenReset) return;
      this.seenReset = n;
      untracked(() => this.show(this.page() ?? (this.receiptsOnly() ? 'receipts' : 'home')));
    });
  }

  async ngOnInit(): Promise<void> {
    try {
      const [customers, branches, methods] = await Promise.all([
        this.admin.listCustomers(),
        this.admin.listBranches(),
        this.admin.listPaymentMethods(),
        // The catalogue is for raising an invoice, which a cash-box cashier never does.
        this.receiptsOnly() ? Promise.resolve([]) : this.catalog.ensure(),
      ]);
      this.customers.set(customers);
      this.branches.set(branches.filter((b) => b.isActive));
      this.methods.set(methods.filter((m) => m.isActive && !NOT_A_RECEIPT_TENDER.has(m.behavior)));
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      // The chips and the children still render on a failed load; their selects simply have
      // nothing but the dash to offer, and the banner above says why.
      this.loaded.set(true);
      const page = this.page() ?? (this.receiptsOnly() ? 'receipts' : null);
      // The receipts page is already where its address says; anything else opens on its chip.
      if (page && !this.addressed()) this.show(page);
      if (page) this.section.set(page);
      const start = this.start();
      if (start) this.view.set({ kind: 'account', customerId: start, part: 'statement' });
    }
  }

  show(section: Section): void {
    this.section.set(section);
    this.go({ kind: section });
  }

  go(view: View): void {
    this.justSaved.set(null);
    const url = this.addressed() ? receiptUrl(view) : null;
    if (url) {
      void this.router.navigate(url.path, { queryParams: url.query });
      window.scrollTo({ top: 0 });
      return;
    }
    // A view with no address of its own yet (an invoice, an account) replaces the screen in place.
    if (this.addressed()) this.setHeading(null);
    this.view.set(view);
    // A document screen replaces a list the reader had scrolled; landing halfway down the new
    // one hides its heading and its warning.
    window.scrollTo({ top: 0 });
  }

  /**
   * A customer's account. On the receipts page it is the Customers module's page, at its own
   * address (`customers/accounts?customer=`); elsewhere it opens in place as before.
   */
  openAccount(customerId: string, part: AccountPart): void {
    if (this.addressed()) {
      void this.router.navigate(['/backoffice', 'customers', 'accounts'], { queryParams: { customer: customerId } });
      return;
    }
    this.go({ kind: 'account', customerId, part });
  }

  private setHeading(heading: string | null): void {
    this.chrome?.heading.set(heading);
    this.chrome?.description.set(null);
    this.chrome?.trail.set([]);
  }

  invoiceSaved(saved: CreateInvoiceResponse): void {
    const { invoice, warning } = saved;
    this.go({
      kind: 'invoice',
      id: invoice.id,
      customerId: invoice.customerId,
      done: this.i18n.t('inv.form.saved', { n: invoice.number }),
      warning,
    });
  }

  /** Back to where the form was started from: the account when there was one, else the list. */
  invoiceCancelled(customerId: string | null): void {
    if (customerId) this.go({ kind: 'account', customerId, part: 'invoices' });
    else this.go({ kind: 'invoices' });
  }

  creditNoteSaved(note: CreditNote, customerId: string): void {
    this.go({ kind: 'invoice', id: note.invoiceId, customerId });
    this.offerPrint('credit-note', note.id, this.i18n.t('inv.credit.saved', { n: note.number }));
  }

  receiptSaved(voucher: ReceiptVoucher): void {
    // A pending voucher settles nothing; the sentence says so before the screen does.
    const key = voucher.status === 'Pending' ? 'inv.rcpt.form.savedPending' : 'inv.rcpt.form.saved';
    this.go({ kind: 'receipt', id: voucher.id, customerId: voucher.customerId });
    this.offerPrint('receipt-voucher', voucher.id, this.i18n.t(key, { n: voucher.number }));
  }

  /** The done sentence and the paper it is about, shown together until the reader moves on. */
  private offerPrint(kind: PrintKind, id: string, label: string): void {
    this.justSaved.set({ kind, id, label });
  }

  receiptCancelled(ref: { customerId: string | null; invoiceId: string | null }): void {
    if (ref.invoiceId && ref.customerId) {
      this.go({ kind: 'invoice', id: ref.invoiceId, customerId: ref.customerId });
    } else if (ref.customerId) {
      this.go({ kind: 'account', customerId: ref.customerId, part: 'receipts' });
    } else {
      this.go({ kind: 'receipts' });
    }
  }
}

const RECEIPTS_URL = ['/backoffice', 'finance', 'receipts'];

/**
 * The view an address under `finance/receipts` names; anything unrecognised is the list.
 * `?act=bounce` opens a receipt on its bounce form; `new?customer=&invoice=` starts the form on
 * them, as a button in an account or on an invoice does.
 */
export function receiptViewFor(
  path: readonly string[],
  q: { act?: string | null; customer?: string | null; invoice?: string | null } = {},
): View {
  const [first] = path;
  if (!first || path.length > 1) return { kind: 'receipts' };
  if (first === 'new') return { kind: 'newReceipt', customerId: q.customer ?? null, invoiceId: q.invoice ?? null };
  // The customer comes with the receipt when it loads; the address does not need to carry it.
  return { kind: 'receipt', id: first, customerId: '', outcome: q.act === 'bounce' ? 'Bounced' : null };
}

/** The address of a receipts-page view, or null for a view that has none yet. */
export function receiptUrl(view: View): { path: string[]; query: Record<string, string> } | null {
  switch (view.kind) {
    case 'receipts':
      return { path: RECEIPTS_URL, query: {} };
    case 'newReceipt': {
      const query: Record<string, string> = {};
      if (view.customerId) query['customer'] = view.customerId;
      if (view.invoiceId) query['invoice'] = view.invoiceId;
      return { path: [...RECEIPTS_URL, 'new'], query };
    }
    case 'receipt':
      return { path: [...RECEIPTS_URL, view.id], query: view.outcome === 'Bounced' ? { act: 'bounce' } : {} };
    default:
      return null;
  }
}
