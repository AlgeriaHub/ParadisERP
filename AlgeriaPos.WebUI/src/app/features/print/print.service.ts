import { Injectable } from '@angular/core';
import { Lang } from '../../core/models';

/** The documents the print route knows how to draw; `/print/:kind/:id`. */
export type PrintKind =
  | 'invoice'
  | 'credit-note'
  | 'receipt-voucher'
  | 'statement'
  | 'ageing'
  | 'rental-contract'
  | 'issue-note'
  | 'return-note'
  | 'payslip'
  | 'payment-voucher'
  /** Customer quantity statement; the id is the customer. */
  | 'quantity-statement'
  | 'stock-transfer'
  /** A back-office report; the id is its name (print-reports.ts `ReportName`). */
  | 'report';

export interface PrintOptions {
  /** Document language; the page defaults to the interface language and offers a choice. */
  lang?: Lang;
  /** How many copies to lay out; the voucher and the contract default to two. */
  copies?: number;
  /** Call window.print() as soon as the data and the fonts are in. */
  auto?: boolean;
  /** Statement: the window it covers, as plain dates (`yyyy-MM-dd`), both included; the server reads them in the company's zone. */
  from?: string;
  to?: string;
  /** Statement: all, sales or rentals (the admin may narrow it; the server checks). */
  kind?: string;
  /** Ageing: the day the buckets are counted at; omitted means the server's today. */
  asOf?: string;
  /**
   * Issue and return notes: the contract they belong to. A note is only readable through its
   * contract's route, and the shared `/print/:kind/:id` shape has one id — so the note is the
   * id and the contract travels beside it rather than in a second path segment nobody else needs.
   */
  contract?: string;
  /**
   * Payslip: one sheet instead of the whole run. The id is the run — a payslip has no life
   * outside the month it was computed in — and this is the run line to draw. Omitted, the tab
   * draws every line of the run, which is how a branch prints payday in one go.
   */
  line?: string;
  /** Report: the branch the figures are for; omitted is the whole group. */
  branch?: string | null;
  /** Account statement: the account. */
  account?: string | null;
  /** Journal: one kind of document only. */
  source?: string | null;
  /** Quantity statement: the "current quantities" view (no dates). */
  current?: boolean;
  /** Quantity statement: one site, a contract status as of `to`, a search, some items. */
  site?: string | null;
  status?: string | null;
  search?: string | null;
  products?: string[];
  /** Item movement & status: encoded filter query string and optional item id. */
  filters?: string;
  item?: string;
}

/**
 * Opens a document in its own tab (docs/07 §4.2). Every Print button calls this instead of
 * `window.print()`: the screen behind the button is a working screen with chips and forms,
 * not paper, and the same route is what a server-side renderer would drive later.
 */
@Injectable({ providedIn: 'root' })
export class PrintService {
  open(kind: PrintKind, id: string, opts: PrintOptions = {}): void {
    window.open(this.url(kind, id, opts), '_blank', 'noopener');
  }

  /** The route as a full URL, built on the app's own location so it works under any base path. */
  url(kind: PrintKind, id: string, opts: PrintOptions = {}): string {
    const params = new URLSearchParams();
    if (opts.lang) params.set('lang', opts.lang);
    if (opts.copies && opts.copies > 1) params.set('copies', String(opts.copies));
    if (opts.auto) params.set('auto', '1');
    if (opts.from) params.set('from', opts.from);
    if (opts.to) params.set('to', opts.to);
    if (opts.kind && opts.kind !== 'all') params.set('kind', opts.kind);
    if (opts.asOf) params.set('asOf', opts.asOf);
    if (opts.contract) params.set('contract', opts.contract);
    if (opts.line) params.set('line', opts.line);
    if (opts.branch) params.set('branch', opts.branch);
    if (opts.account) params.set('account', opts.account);
    if (opts.source) params.set('source', opts.source);
    if (opts.current) params.set('current', '1');
    if (opts.site) params.set('site', opts.site);
    if (opts.status && opts.status !== 'all') params.set('status', opts.status);
    if (opts.search?.trim()) params.set('search', opts.search.trim());
    if (opts.products?.length) params.set('products', opts.products.join(','));
    if (opts.filters) params.set('filters', opts.filters);
    if (opts.item) params.set('item', opts.item);
    const query = params.toString();
    const { origin, pathname, search } = window.location;
    // Hash routing: the document's own query goes after the hash so the router sees it.
    return `${origin}${pathname}${search}#/print/${kind}/${encodeURIComponent(id)}${query ? '?' + query : ''}`;
  }
}
