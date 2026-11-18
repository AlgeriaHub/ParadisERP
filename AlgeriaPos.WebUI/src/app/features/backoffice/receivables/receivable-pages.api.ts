import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';
import type { TaxMode } from '../../../core/models';
import { PageResult, pageQueryString } from '../../../core/paging';
import type { PaymentVoucher, PaymentVoucherKind, PaymentVoucherStatus } from './payment-vouchers-api.service';
import type {
  CreditNote,
  CustomerAgeing,
  CustomerStatement,
  CustomerStatementRow,
  DebtAgeing,
  Invoice,
  InvoiceOrigin,
  ReceiptStatus,
  ReceiptVoucher,
  StatementFilter,
} from './receivables-api.service';

/**
 * The debtor book's lists and payment vouchers a page at a time (owner, 2026-11-17: "I need to
 * paginate in backend, not frontend"). Each `…/page` route sits beside the list's own route (left
 * as it was for the forms, the pickers and the papers), runs the screen's filters on the server
 * over every row and sends one page, with the list's figures over all its rows in `summary`. A
 * filter at "all", empty or null is not sent (`pageQueryString`). Dates are plain days
 * (`yyyy-MM-dd`, both included), read in the company's zone by the server.
 */

/** The invoices list's server filters; `state` is the chip (open: still owed; overdue: owed and late). */
export interface InvoicesPageQuery {
  skip: number;
  take: number;
  customerId?: string | null;
  from?: string | null;
  to?: string | null;
  state?: 'all' | 'open' | 'overdue' | 'settled' | 'credited';
  q?: string;
  origin?: 'all' | InvoiceOrigin;
  taxMode?: 'all' | TaxMode;
  paper?: 'all' | 'yes' | 'no';
}

/** The invoices tiles over every invoice the filters keep, and the choices the window's invoices offer. */
export interface InvoicesPageSummary {
  openCount: number;
  outstanding: number;
  overdue: number;
  claimed: number;
  origins: InvoiceOrigin[];
  taxModes: TaxMode[];
}

/** The receipts list's server filters. */
export interface ReceiptsPageQuery {
  skip: number;
  take: number;
  customerId?: string | null;
  status?: ReceiptStatus | null;
  branchId?: string | null;
  q?: string;
  method?: string;
  due?: string;
  unallocated?: 'all' | 'yes' | 'no';
}

/** The receipts tiles and choices, over the receipts of the state, customer and branch asked for. */
export interface ReceiptsPageSummary {
  pendingTotal: number;
  dueTotal: number;
  unallocatedTotal: number;
  methodKeys: string[];
  hasPending: boolean;
}

/** The credit notes' totals line, over every note. */
export interface CreditNotesPageSummary {
  ht: number;
  tax: number;
  ttc: number;
}

/** A customer statement without its rows; `pageOpening` is the running balance before the page's first row. */
export type CustomerStatementHead = Omit<CustomerStatement, 'rows'> & { pageOpening: number };

/** The whole book's ageing figures, and which customers the board holds. */
export type AgeingPageSummary = Omit<DebtAgeing, 'customers'> & { customerCount: number; listedCustomerIds: string[] };

/** The payment vouchers list's server filters (`status` and `kind` are the server's numbers). */
export interface PaymentVouchersPageQuery {
  skip: number;
  take: number;
  branchId?: string | null;
  status?: PaymentVoucherStatus | null;
  kind?: PaymentVoucherKind | 'all';
  method?: string;
  from?: string;
  to?: string;
  q?: string;
}

/** The two queues' totals (the branch's pending and approved, whatever the filters) and the choices offered. */
export interface PaymentVouchersPageSummary {
  pendingTotal: number;
  approvedTotal: number;
  kinds: PaymentVoucherKind[];
  methods: { key: string; name: string }[];
}

@Injectable({ providedIn: 'root' })
export class ReceivablePagesApi {
  private readonly api = inject(ApiService);

  /** What is owed and oldest first, then the newest. */
  invoicesPage(query: InvoicesPageQuery): Promise<PageResult<Invoice, InvoicesPageSummary>> {
    return this.api.getJson<PageResult<Invoice, InvoicesPageSummary>>(`/api/receivables/invoices/page${pageQueryString({ ...query })}`);
  }

  /** Oldest due first, as the list route orders them. */
  receiptsPage(query: ReceiptsPageQuery): Promise<PageResult<ReceiptVoucher, ReceiptsPageSummary>> {
    return this.api.getJson<PageResult<ReceiptVoucher, ReceiptsPageSummary>>(`/api/receivables/receipts/page${pageQueryString({ ...query })}`);
  }

  /** Newest first. */
  creditNotesPage(query: { skip: number; take: number; customerId: string }): Promise<PageResult<CreditNote, CreditNotesPageSummary>> {
    return this.api.getJson<PageResult<CreditNote, CreditNotesPageSummary>>(
      `/api/receivables/credit-notes/page${pageQueryString({ ...query })}`,
    );
  }

  /** A page of account 411 for one customer; the head comes with every page. */
  statementPage(query: {
    skip: number;
    take: number;
    customerId: string;
    from: string;
    to: string;
    kind: StatementFilter;
  }): Promise<PageResult<CustomerStatementRow, CustomerStatementHead>> {
    return this.api.getJson<PageResult<CustomerStatementRow, CustomerStatementHead>>(
      `/api/receivables/statement/page${pageQueryString({ ...query })}`,
    );
  }

  /** The ageing board, latest first then largest; `asOf` is an instant (midday of the chosen day), or none for now. */
  ageingPage(query: { skip: number; take: number; asOf?: string | null; q?: string }): Promise<PageResult<CustomerAgeing, AgeingPageSummary>> {
    return this.api.getJson<PageResult<CustomerAgeing, AgeingPageSummary>>(`/api/receivables/ageing/page${pageQueryString({ ...query })}`);
  }

  /** Newest first. */
  paymentVouchersPage(query: PaymentVouchersPageQuery): Promise<PageResult<PaymentVoucher, PaymentVouchersPageSummary>> {
    return this.api.getJson<PageResult<PaymentVoucher, PaymentVouchersPageSummary>>(
      `/api/payment-vouchers/page${pageQueryString({ ...query })}`,
    );
  }
}
