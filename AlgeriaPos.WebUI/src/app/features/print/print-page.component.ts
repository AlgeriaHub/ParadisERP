import { Component, OnDestroy, OnInit, ViewEncapsulation, computed, effect, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { AdminProduct, AdminService, adminErrorMessage } from '../../core/admin.service';
import { ApiError } from '../../core/api.service';
import { I18nService } from '../../core/i18n.service';
import { Lang } from '../../core/models';
import { LedgerService } from '../../core/ledger.service';
import { RegionalService } from '../../core/regional.service';
import { SecureImageService } from '../../core/secure-image.service';
import {
  CreditNote,
  CustomerAgeing,
  CustomerStatement,
  DebtAgeing,
  Invoice,
  ReceiptVoucher,
  ReceivablesApi,
} from '../backoffice/receivables/receivables-api.service';
import { PayrollApi } from '../backoffice/payroll/payroll-api.service';
import { PaymentVoucher, PaymentVouchersApi } from '../backoffice/receivables/payment-vouchers-api.service';
import {
  LabelBook,
  PayrollRun,
  PayrollRunLine,
  PayrollRunsApi,
  emptyLabelBook,
  labelBook,
} from '../backoffice/payroll/payroll-run-view';
import {
  ContractDocuments,
  IssueNote,
  RentalContract,
  QuantityStatement,
  RentalsApi,
  ReturnNote,
} from '../backoffice/rentals/rentals-api.service';
import { PrintAgeingComponent } from './print-ageing.component';
import { PrintCreditNoteComponent } from './print-credit-note.component';
import { PrintInvoiceComponent } from './print-invoice.component';
import { PrintIssueNoteComponent } from './print-issue-note.component';
import { PrintPayslipComponent } from './print-payslip.component';
import { PrintPaymentVoucherComponent } from './print-payment-voucher.component';
import { PrintStockTransferComponent } from './print-stock-transfer.component';
import { StockTransfer, StockTransfersApi } from '../backoffice/stock/stock-transfers-api.service';
import { PrintReceiptVoucherComponent } from './print-receipt-voucher.component';
import { PrintRentalContractComponent } from './print-rental-contract.component';
import { PrintReturnNoteComponent } from './print-return-note.component';
import { PrintStatementComponent } from './print-statement.component';
import { PrintQuantityStatementComponent } from './print-quantity-statement.component';
import { PrintReportComponent } from './print-report.component';
import {
  JOURNAL_PAPER_TAKE,
  ReportName,
  ReportPaper,
  STATEMENT_PAPER_TAKE,
  isReportName,
  reportLabel,
} from './print-reports';
import { INVOICE_TAKE } from '../backoffice/receivables/receivable-view';
import { PrintData, buildPrintContext, documentBranchId } from './print-context';
import { PrintKind } from './print.service';
import { StoreDocumentsApi, StoreDocumentsDto } from './store-documents-api.service';
import { ItemStatusApiService, ItemStatusQuery } from '../backoffice/item-status/item-status-api.service';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';

/** What the route drew, once everything it needs is in. */
type Document =
  | { kind: 'invoice'; invoice: Invoice }
  | { kind: 'credit-note'; note: CreditNote; invoice: Invoice | null }
  | { kind: 'receipt-voucher'; voucher: ReceiptVoucher }
  | {
      kind: 'statement';
      statement: CustomerStatement;
      ageing: CustomerAgeing | null;
      asOf: string | null;
      creditNotes: CreditNote[];
      receipts: ReceiptVoucher[];
    }
  | { kind: 'ageing'; ageing: DebtAgeing }
  | { kind: 'rental-contract'; contract: RentalContract; products: AdminProduct[]; issueNotes: IssueNote[] }
  | { kind: 'issue-note'; docs: ContractDocuments; note: IssueNote; products: AdminProduct[] }
  | { kind: 'return-note'; docs: ContractDocuments; note: ReturnNote; products: AdminProduct[] }
  | { kind: 'payslip'; run: PayrollRun; lines: PayrollRunLine[]; book: LabelBook }
  | { kind: 'payment-voucher'; voucher: PaymentVoucher }
  | { kind: 'quantity-statement'; statement: QuantityStatement }
  | { kind: 'stock-transfer'; transfer: StockTransfer }
  | { kind: 'report'; report: ReportPaper };

/** Page size per kind; injected at runtime because an @page rule cannot be scoped (docs/07 §4.2). */
const PAGE_SIZE: Record<PrintKind, string> = {
  invoice: 'A4',
  'credit-note': 'A4',
  'receipt-voucher': 'A5',
  statement: 'A4',
  // Eleven columns of money do not fit across 210 mm at a readable size.
  ageing: 'A4 landscape',
  'rental-contract': 'A4',
  'issue-note': 'A4',
  'return-note': 'A4',
  payslip: 'A4',
  // The receipt voucher's mirror, on the same half sheet.
  'payment-voucher': 'A5',
  'stock-transfer': 'A4',
  // Six columns at most, the trial balance's; portrait holds them at the statement's rhythm.
  report: 'A4',
  // Up to ten columns of quantities and money.
  'quantity-statement': 'A4 landscape',
};

const DEFAULT_COPIES: Record<PrintKind, number> = {
  invoice: 1,
  'credit-note': 1,
  'receipt-voucher': 2,
  statement: 1,
  ageing: 1,
  // One by default (owner, 2026-10-10); the copies box asks for the second when the company
  // keeps one and the hirer takes one.
  'rental-contract': 1,
  'issue-note': 1,
  'return-note': 1,
  // One sheet per employee already; a second copy is a second stack of nine hundred pages.
  payslip: 1,
  // The company keeps one, the payee signs and takes the other.
  'payment-voucher': 2,
  'stock-transfer': 1,
  report: 1,
  'quantity-statement': 1,
};

/** The kinds whose copy count is the clerk's to change; the rest are one sheet by nature. */
const COPYABLE: readonly PrintKind[] = ['receipt-voucher', 'rental-contract', 'payment-voucher'];

/**
 * `/print/:kind/:id` — one document in its own tab, without the app's chrome, in the language
 * chosen at print time (docs/07 §4.2).
 *
 * <p>The tab is data-only: it reads the document and the company's identity and draws, with no
 * click needed to render, which is exactly what a server-side renderer would drive later over
 * the same URL. `?auto=1` calls the print dialog once the data and the fonts are in.</p>
 *
 * <p>The toolbar at the top is the clerk's, in the interface language, and does not print. The
 * sheet below it is the customer's, in the document language: `dir` and `lang` are set on its
 * root, not on <html>, so switching the document to Arabic never turns the clerk's tab around.</p>
 *
 * <p>Two kinds carry a second id. A statement is a customer over a window (`from`, `to`), and an
 * issue or return note is only readable through its contract (`contract`) — so those travel as
 * query parameters rather than as a second path segment the other six kinds would never use.</p>
 */
@Component({
  selector: 'app-print-page',
  standalone: true,
  imports: [
    PrintInvoiceComponent,
    PrintCreditNoteComponent,
    PrintReceiptVoucherComponent,
    PrintStatementComponent,
    PrintQuantityStatementComponent,
    PrintAgeingComponent,
    PrintRentalContractComponent,
    PrintIssueNoteComponent,
    PrintReturnNoteComponent,
    PrintPayslipComponent,
    PrintPaymentVoucherComponent,
    PrintStockTransferComponent,
    PrintReportComponent,
  ],
  encapsulation: ViewEncapsulation.None,
  styleUrl: './print.scss',
  template: `
    <div class="print-bar no-print" [attr.dir]="i18n.dir()">
      <label>
        {{ i18n.t('print.lang') }}
        <select [value]="lang()" (change)="pickLang($event)" [attr.aria-label]="i18n.t('print.lang')">
          @for (l of i18n.langs(); track l.code) {
            <option [value]="l.code" [selected]="l.code === lang()">{{ l.label }}</option>
          }
        </select>
      </label>
      @if (copyable()) {
        <label>
          {{ i18n.t('print.copies') }}
          <input type="number" min="1" max="4" step="1" class="num mono" [value]="copies()" (change)="pickCopies($event)" />
        </label>
      }
      <span class="spacer"></span>
      @if (document()) {
        <span class="print-note">{{ i18n.t('print.saveHint') }}</span>
      }
      <button type="button" class="btn-ghost" (click)="close()">{{ i18n.t('common.close') }}</button>
      <button type="button" class="btn-primary" [disabled]="!document()" (click)="print()">{{ i18n.t('common.print') }}</button>
    </div>

    @if (error()) {
      <p class="notice danger no-print" role="alert" [attr.dir]="i18n.dir()">{{ error() }}</p>
    } @else if (notFound()) {
      <p class="notice danger no-print" role="alert" [attr.dir]="i18n.dir()">{{ i18n.t('print.notFound') }}</p>
    } @else if (!document()) {
      <p class="notice no-print" role="status" [attr.dir]="i18n.dir()">{{ i18n.t('common.loading') }}</p>
    }

    @if (ctx(); as c) {
      @if (document(); as doc) {
        <div class="print-page" [class.print-page--landscape]="landscape()" [attr.dir]="c.dir" [attr.lang]="c.lang">
          @switch (doc.kind) {
            @case ('invoice') {
              <app-print-invoice [ctx]="c" [invoice]="doc.invoice" [copies]="copies()" />
            }
            @case ('credit-note') {
              <app-print-credit-note [ctx]="c" [note]="doc.note" [invoice]="doc.invoice" [copies]="copies()" />
            }
            @case ('receipt-voucher') {
              <app-print-receipt-voucher [ctx]="c" [voucher]="doc.voucher" [copies]="copies()" />
            }
            @case ('statement') {
              <app-print-statement
                [ctx]="c"
                [statement]="doc.statement"
                [ageing]="doc.ageing"
                [asOf]="doc.asOf"
                [creditNotes]="doc.creditNotes"
                [receipts]="doc.receipts"
                [copies]="copies()"
              />
            }
            @case ('quantity-statement') {
              <app-print-quantity-statement [ctx]="c" [statement]="doc.statement" [copies]="copies()" />
            }
            @case ('ageing') {
              <app-print-ageing [ctx]="c" [ageing]="doc.ageing" [copies]="copies()" />
            }
            @case ('rental-contract') {
              <app-print-rental-contract
                [ctx]="c"
                [contract]="doc.contract"
                [products]="doc.products"
                [issueNotes]="doc.issueNotes"
                [copies]="copies()"
              />
            }
            @case ('issue-note') {
              <app-print-issue-note [ctx]="c" [docs]="doc.docs" [note]="doc.note" [products]="doc.products" [copies]="copies()" />
            }
            @case ('return-note') {
              <app-print-return-note [ctx]="c" [docs]="doc.docs" [note]="doc.note" [products]="doc.products" [copies]="copies()" />
            }
            @case ('payslip') {
              <app-print-payslip [ctx]="c" [run]="doc.run" [lines]="doc.lines" [book]="doc.book" />
            }
            @case ('payment-voucher') {
              <app-print-payment-voucher [ctx]="c" [voucher]="doc.voucher" [copies]="copies()" />
            }
            @case ('stock-transfer') {
              <app-print-stock-transfer [ctx]="c" [transfer]="doc.transfer" />
            }
            @case ('report') {
              <app-print-report [ctx]="c" [report]="doc.report" />
            }
          }
          <div class="print-strip mono" aria-hidden="true">{{ number() }}</div>
        </div>
      }
    }
  `,
})
export class PrintPageComponent implements OnInit, OnDestroy {
  readonly i18n = inject(I18nService);
  /** The back-office vocabulary travels in this route's chunk, never in a till's download.
   *  A field initialiser, so the keys are in place before the first render. */
  private readonly words = this.i18n.extend(BACKOFFICE_TRANSLATIONS);

  private readonly regional = inject(RegionalService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly admin = inject(AdminService);
  private readonly receivables = inject(ReceivablesApi);
  private readonly rentals = inject(RentalsApi);
  private readonly storeDocuments = inject(StoreDocumentsApi);
  private readonly images = inject(SecureImageService);
  private readonly payroll = inject(PayrollRunsApi);
  private readonly payrollSettings = inject(PayrollApi);
  private readonly paymentVouchers = inject(PaymentVouchersApi);
  private readonly stockTransfers = inject(StockTransfersApi);
  private readonly ledger = inject(LedgerService);
  private readonly itemStatusApi = inject(ItemStatusApiService);

  readonly kind = signal<PrintKind | null>(null);
  readonly id = signal('');
  readonly lang = signal<Lang>(this.i18n.lang());
  readonly copies = signal(1);
  readonly data = signal<PrintData | null>(null);
  readonly document = signal<Document | null>(null);
  readonly error = signal<string | null>(null);
  readonly notFound = signal(false);

  readonly ctx = computed(() => {
    const data = this.data();
    // The document's branch rides in the context, so the header prints that branch's own fiscal
    // identifiers on every kind without each document having to pass it.
    return data
      ? buildPrintContext(this.i18n, this.regional.settings(), this.lang(), { ...data, branchId: documentBranchId(this.document()) })
      : null;
  });

  readonly landscape = computed(() => {
    const kind = this.kind();
    return !!kind && PAGE_SIZE[kind].includes('landscape');
  });

  readonly copyable = computed(() => {
    const kind = this.kind();
    return !!kind && COPYABLE.includes(kind);
  });

  /** What the foot strip and the saved file are named after; a report has no number of its own. */
  readonly number = computed(() => {
    const doc = this.document();
    if (!doc) return '';
    switch (doc.kind) {
      case 'invoice':
        return doc.invoice.number;
      case 'credit-note':
        return doc.note.number;
      case 'receipt-voucher':
        return doc.voucher.number;
      case 'statement':
        return doc.statement.customerCode;
      case 'quantity-statement':
        return doc.statement.customerCode;
      case 'ageing':
        return doc.ageing.asOf.slice(0, 10);
      case 'rental-contract':
        return doc.contract.number;
      case 'issue-note':
      case 'return-note':
        return doc.note.number;
      // A run of payslips is named after the run; one payslip after the person it belongs to.
      case 'payslip':
        return doc.lines.length === 1 ? doc.lines[0].employeeNumber : doc.run.number;
      case 'payment-voucher':
        return doc.voucher.number;
      case 'stock-transfer':
        return doc.transfer.number;
      // A report has no number: its title and its window name the file and the foot strip.
      case 'report': {
        const ctx = this.ctx();
        return ctx ? reportLabel(ctx, doc.report) : '';
      }
    }
  });

  private pageRule: HTMLStyleElement | null = null;
  private readonly previousTitle = document.title;
  private auto = false;
  private printed = false;

  constructor() {
    // The browser names the saved PDF after the tab: "INV-000012.pdf" rather than "Caisse.pdf".
    effect(() => {
      const number = this.number();
      if (number) document.title = number;
    });
    // Print once, when the paper is actually drawn; never before the data, never twice.
    effect(() => {
      if (!this.auto || this.printed || !this.document() || !this.ctx()) return;
      this.printed = true;
      void this.printWhenReady();
    });
  }

  async ngOnInit(): Promise<void> {
    const params = this.route.snapshot.paramMap;
    const query = this.route.snapshot.queryParamMap;
    const kind = params.get('kind') as PrintKind | null;
    const id = params.get('id') ?? '';
    if (!kind || !(kind in PAGE_SIZE) || !id) {
      this.error.set(this.i18n.t('print.unknownKind'));
      return;
    }
    this.kind.set(kind);
    this.id.set(id);
    this.injectPageSize(PAGE_SIZE[kind]);

    const wanted = query.get('lang');
    if (wanted && this.i18n.langs().some((l) => l.code === wanted)) this.lang.set(wanted);
    const copies = Number(query.get('copies'));
    this.copies.set(Number.isInteger(copies) && copies >= 1 && copies <= 4 ? copies : DEFAULT_COPIES[kind]);
    this.auto = query.get('auto') === '1';

    await this.load(kind, id, query);
  }

  ngOnDestroy(): void {
    this.pageRule?.remove();
    this.pageRule = null;
    document.title = this.previousTitle;
  }

  /**
   * The logo is a signed-in link; paper carries it inline instead. It is fetched here, inside the
   * same wait as the rest of the data, so the sheet is drawn — and `?auto=1` prints — only once
   * it is in: a print dialog opened before a second request came back prints an empty frame. A
   * logo that cannot be had prints no logo, never a broken image.
   */
  private async inlineLogo(documents: StoreDocumentsDto): Promise<StoreDocumentsDto> {
    return { ...documents, logoUrl: await this.images.dataUrl(documents.logoUrl) };
  }

  private async load(kind: PrintKind, id: string, query: { get(name: string): string | null }): Promise<void> {
    try {
      // The identity block and the lookups first, the document itself in the same breath: the
      // page renders once, complete, rather than a head without a body.
      const [store, documents, branches, customers, methods, doc] = await Promise.all([
        this.admin.getStore(),
        // A manager may read the texts; a very old server has no route at all — then no footer text.
        this.storeDocuments.get().then((documents) => this.inlineLogo(documents)).catch(() => null),
        this.admin.listBranches(),
        this.admin.listCustomers(),
        this.admin.listPaymentMethods().catch(() => []),
        this.loadDocument(kind, id, query),
      ]);
      this.data.set({ store, documents, branches, customers, methods });
      this.document.set(doc);
    } catch (e) {
      // An unknown id answers 404 with an empty body, which adminErrorMessage can only call "HTTP 404".
      if (e instanceof ApiError && e.status === 404) this.notFound.set(true);
      else this.error.set(adminErrorMessage(e));
    }
  }

  private async loadDocument(kind: PrintKind, id: string, query: { get(name: string): string | null }): Promise<Document> {
    switch (kind) {
      case 'invoice':
        return { kind, invoice: await this.receivables.getInvoice(id) };
      case 'credit-note': {
        const note = await this.receivables.getCreditNote(id);
        // The corrected invoice carries the frozen buyer block; if it cannot be read the note
        // still prints, with the customer row instead.
        const invoice = await this.receivables.getInvoice(note.invoiceId).catch(() => null);
        return { kind, note, invoice };
      }
      case 'receipt-voucher':
        return { kind, voucher: await this.receivables.getReceipt(id) };
      case 'payment-voucher':
        return { kind, voucher: await this.paymentVouchers.get(id) };
      case 'quantity-statement': {
        // The same query as the screen, so the sheet carries the same figures.
        const current = query.get('current') === '1';
        const statement = await this.rentals.quantityStatement({
          customerId: id,
          current,
          from: current ? null : query.get('from'),
          to: current ? null : query.get('to'),
          contractId: query.get('contract'),
          branchId: query.get('branch'),
          siteId: query.get('site'),
          status: query.get('status'),
          search: query.get('search'),
          productIds: (query.get('products') ?? '').split(',').filter((x) => !!x),
        });
        return { kind, statement };
      }
      case 'stock-transfer':
        return { kind, transfer: await this.stockTransfers.get(id) };
      case 'statement': {
        const kindRaw = query.get('kind');
        const statementKind = kindRaw === 'sales' || kindRaw === 'rentals' ? kindRaw : 'all';
        const statement = await this.receivables.statement(id, query.get('from'), query.get('to'), statementKind);
        // The strip is dated to the end of the window unless the caller named a day: an ageing
        // taken today beside a statement that closed in March would state two different debts.
        const asOf = query.get('asOf') ?? lastDayOf(statement.to);
        const [ageing, creditNotes, receipts] = await Promise.all([
          this.receivables
            .ageing(asOf, id)
            .then((a) => a.customers.find((c) => c.customerId === id) ?? null)
            // A customer who owes nothing is not in the ageing at all, and the strip simply goes.
            .catch(() => null),
          // What the rows' detail is read from, as on screen: without them a credit note does not
          // say which invoice it corrects and a receipt does not say how it was paid — the sheet
          // still prints.
          this.receivables.listCreditNotes({ customerId: id, take: INVOICE_TAKE }).catch((): CreditNote[] => []),
          this.receivables.listReceipts({ customerId: id, take: INVOICE_TAKE }).catch((): ReceiptVoucher[] => []),
        ]);
        return { kind, statement, ageing, asOf, creditNotes, receipts };
      }
      case 'ageing':
        return { kind, ageing: await this.receivables.ageing(query.get('asOf')) };
      case 'rental-contract': {
        // The issue notes start each line's period on the company's own contract ("تاريخ من");
        // a contract not issued yet, or one whose notes cannot be read, counts from its signing.
        const [contract, products, issueNotes] = await Promise.all([
          this.rentals.getContract(id),
          this.admin.listProducts().catch(() => []),
          this.rentals.listIssueNotes(id).catch(() => [] as IssueNote[]),
        ]);
        return { kind, contract, products, issueNotes };
      }
      case 'issue-note':
      case 'return-note': {
        const contractId = query.get('contract');
        // Without the contract there is nothing to join the note's quantities to.
        if (!contractId) throw new ApiError('', 404);
        const [docs, products] = await Promise.all([
          this.rentals.readContract(contractId),
          this.admin.listProducts().catch(() => []),
        ]);
        if (kind === 'issue-note') {
          const note = docs.issueNotes.find((n) => n.id === id);
          if (!note) throw new ApiError('', 404);
          return { kind, docs, note, products };
        }
        const note = docs.returnNotes.find((n) => n.id === id);
        if (!note) throw new ApiError('', 404);
        return { kind, docs, note, products };
      }
      case 'payslip': {
        // The id is the run: a payslip has no life outside the month it was computed in. One
        // line id in the query prints that person alone; without it the whole branch prints,
        // a hundred lines per request, which is what payday actually needs.
        const [detail, lines, current] = await Promise.all([
          this.payroll.read(id),
          this.payroll.payslipLines(id, query.get('line')),
          // Only the fallback, for a run that never computed and so froze no words of its own.
          this.payrollSettings.settings().then((v) => v.settings).catch(() => null),
        ]);
        if (!lines.length) throw new ApiError('', 404);
        // The run's own snapshot first. The amounts are frozen on the line; the words have to be
        // frozen with them, or reprinting March's payslips in June prints June's wording — and an
        // allowance deleted since prints as its raw key where the paper said a sentence.
        const words = detail.settingsSnapshot ?? current;
        return { kind, run: detail.run, lines, book: words ? labelBook(words) : emptyLabelBook() };
      }
      case 'report':
        if (!isReportName(id)) throw new ApiError('', 404);
        return { kind, report: await this.loadReport(id, query) };
    }
  }

  /**
   * A report read again with the filters the screen sent: the period (`from` inclusive, `to`
   * exclusive, as the wire has them), the branch, and the account or the kind of document where
   * the report takes one. The tab asks for the server's row ceiling rather than the screen's.
   */
  private async loadReport(name: ReportName, query: { get(name: string): string | null }): Promise<ReportPaper> {
    const printedAt = new Date().toISOString();
    if (name === 'item-status') {
      const filtersRaw = query.get('filters') ?? '';
      const filterParams = new URLSearchParams(filtersRaw);
      const q: ItemStatusQuery = {
        branchId: filterParams.get('branchId') ?? query.get('branch') ?? undefined,
        q: filterParams.get('q') ?? undefined,
        productIds: filterParams.get('productIds') ? filterParams.get('productIds')!.split(',') : undefined,
        itemType: (filterParams.get('itemType') as ItemStatusQuery['itemType']) ?? undefined,
        status: (filterParams.get('status') as ItemStatusQuery['status']) ?? undefined,
        customerId: filterParams.get('customerId') ?? undefined,
        contractId: filterParams.get('contractId') ?? undefined,
        includeInactive: filterParams.get('includeInactive') === 'true' ? true : undefined,
      };
      const itemId = query.get('item') || filterParams.get('item') || undefined;
      const detailFrom = filterParams.get('from') ?? query.get('from') ?? undefined;
      const detailTo = filterParams.get('to') ?? query.get('to') ?? undefined;
      const [report, detail] = await Promise.all([
        this.itemStatusApi.summary(q),
        itemId ? this.itemStatusApi.detail(itemId, q, detailFrom, detailTo) : Promise.resolve(null),
      ]);
      const branchId = q.branchId || null;
      return { name, branchId, printedAt, report, detail };
    }

    const from = query.get('from');
    const to = query.get('to');
    const branchId = query.get('branch') || null;
    // Every report needs the end of its window; all but the balance sheet need its start too.
    if (!to || (name !== 'balance-sheet' && !from)) throw new ApiError('', 404);
    const period = { from: from ?? '', to, branchId };
    switch (name) {
      case 'trial-balance':
        return { name, branchId, printedAt, trial: await this.ledger.trialBalance(period) };
      case 'profit-loss':
        return { name, branchId, printedAt, pl: await this.ledger.profitAndLoss(period) };
      case 'balance-sheet':
        return { name, branchId, printedAt, sheet: await this.ledger.balanceSheet({ asOf: to, branchId }) };
      case 'account-statement': {
        const accountId = query.get('account');
        if (!accountId) throw new ApiError('', 404);
        const statement = await this.ledger.accountStatement({ ...period, accountId, take: STATEMENT_PAPER_TAKE });
        return { name, branchId, printedAt, statement, capped: statement.rows.length >= STATEMENT_PAPER_TAKE };
      }
      case 'journal': {
        const source = query.get('source') || null;
        const [entries, accounts] = await Promise.all([
          this.ledger.listEntries({ ...period, sourceType: source, take: JOURNAL_PAPER_TAKE }),
          // The names in the document language; without them the lines keep their French names.
          this.ledger.listAccounts(true).catch(() => []),
        ]);
        return { name, branchId, printedAt, from: period.from, to, source, entries, accounts, capped: entries.length >= JOURNAL_PAPER_TAKE };
      }
      case 'sales': {
        const [summary, products] = await Promise.all([
          this.admin.getSalesSummary(period.from, to, branchId),
          this.admin.listProducts().catch(() => []),
        ]);
        return { name, branchId, printedAt, summary, products };
      }
    }
  }

  pickLang(event: Event): void {
    const lang = (event.target as HTMLSelectElement).value;
    this.lang.set(lang);
    void this.router.navigate([], { relativeTo: this.route, queryParams: { lang }, queryParamsHandling: 'merge', replaceUrl: true });
  }

  pickCopies(event: Event): void {
    const n = Number((event.target as HTMLInputElement).value);
    const copies = Number.isInteger(n) && n >= 1 && n <= 4 ? n : 1;
    this.copies.set(copies);
    void this.router.navigate([], { relativeTo: this.route, queryParams: { copies }, queryParamsHandling: 'merge', replaceUrl: true });
  }

  print(): void {
    void this.printWhenReady();
  }

  close(): void {
    window.close();
  }

  /** Fonts first: a print dialog opened before the Arabic face has arrived prints fallback glyphs. */
  private async printWhenReady(): Promise<void> {
    try {
      await document.fonts.ready;
    } catch {
      /* a browser without the API prints with what it has */
    }
    setTimeout(() => window.print(), 50);
  }

  /**
   * The runtime @page for this kind. Injected after the stylesheets, so it wins over the A4
   * default in styles.scss. To verify (docs/07 §4.2): Chromium honours an @page injected after
   * load both in its dialog and in page.pdf({ preferCSSPageSize: true }).
   */
  private injectPageSize(size: string): void {
    const margin = size === 'A5' ? '8mm 9mm' : size.includes('landscape') ? '10mm 12mm' : '14mm 12mm';
    // Every paper's own page margins (owner, 2026-11-09: the browser printed its date and the page's address on a
    // client's contract). Once a page defines margin boxes, Chromium leaves out its own header and footer on those
    // edges: an empty one at the top, and at the foot which page of how many it is — a contract or a report runs to
    // several sheets and is filed loose. Checked against Chrome's own PDF output. A browser without margin boxes
    // prints no count, and its header follows its print dialog as before.
    const boxes =
      ' @top-center { content: ""; } @bottom-center { content: counter(page) " / " counter(pages); font-size: 9pt; color: #444; }';
    const style = document.createElement('style');
    style.id = 'print-page-size';
    style.textContent = `@media print { @page { size: ${size}; margin: ${margin};${boxes} } }`;
    document.head.appendChild(style);
    this.pageRule = style;
  }
}

/**
 * The statement window's exclusive end as the last day it actually covers, at midday so no time
 * zone moves it into the following day.
 */
function lastDayOf(toIso: string): string {
  const end = new Date(/(Z|[+-]\d\d:?\d\d)$/.test(toIso) ? toIso : `${toIso}Z`);
  end.setUTCDate(end.getUTCDate() - 1);
  end.setUTCHours(12, 0, 0, 0);
  return end.toISOString();
}
