import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../core/api.service';

/**
 * The company's printed-document settings (docs/07 §4.3), behind
 * `GET/PUT /api/catalog/store/documents`.
 *
 * <p>Deliberately not on `AdminStore`: these texts are not on the till's `StoreDto` and never
 * replicate, so the client keeps them apart too. Read by anyone who prints (a manager's invoice
 * carries the terms); written by the Admin alone — the PUT answers 403 to a manager.</p>
 */

export type DocumentTextKind =
  | 'invoice'
  | 'creditNote'
  | 'receiptVoucher'
  | 'statement'
  | 'rentalContract'
  | 'issueNote'
  | 'returnNote'
  | 'payslip'
  | 'paymentVoucher';

/**
 * A key a wording is kept and counted under: one of the kinds, or a named rental-contract wording
 * (`rentalContract.<id>`, PinSay #302), which the server counts and keeps in history as a kind of its own.
 */
export type DocumentTextKey = DocumentTextKind | `rentalContract.${string}`;

/**
 * One named rental-contract wording beside the standard one (owner, PinSay #302, 2026-10-05: "enable
 * multi contracts and enhance the creation to be as template"): what the contract form offers by name.
 * Its text is `documentTexts[key]`, written and counted like any kind's.
 */
export interface ContractWording {
  key: `rentalContract.${string}`;
  /** `{ [lang]: name }`; at least one. */
  names: Record<string, string>;
}

/** The server's limits on the list (DocumentTextKinds.MaxContractWordings / MaxContractWordingName). */
export const MAX_CONTRACT_WORDINGS = 20;
export const CONTRACT_WORDING_NAME_MAX = 80;

/**
 * A key for a wording just added: `rentalContract.` and eight lower-case letters or digits, unlike
 * any the list already holds. Chosen here because the PUT sends the list whole, and fixed once
 * saved: the key is what a contract names.
 */
export function newWordingKey(taken: readonly string[], random: () => number = Math.random): `rentalContract.${string}` {
  for (;;) {
    let id = '';
    while (id.length < 8) id += Math.floor(random() * 36).toString(36);
    const key = `rentalContract.${id}` as const;
    if (!taken.includes(key)) return key;
  }
}

export const DOCUMENT_TEXT_KINDS: readonly DocumentTextKind[] = [
  'invoice',
  'creditNote',
  'receiptVoucher',
  'statement',
  'rentalContract',
  'issueNote',
  'returnNote',
  'payslip',
  'paymentVoucher',
];

/** One text may be at most this long; the server refuses more (Store.DocumentTextMaxLength). */
export const DOCUMENT_TEXT_MAX = 4000;

/**
 * What is left of one kind's yearly rewriting allowance. Read-only: the server counts, and a PUT
 * that sent these back would be ignored. The year is the company's own calendar year, and
 * `resetsOnUtc` is its 1 January expressed in UTC, so the screen states the date without doing
 * time-zone arithmetic of its own.
 */
export interface DocumentTextAllowance {
  kind: DocumentTextKey;
  year: number;
  limit: number;
  used: number;
  remaining: number;
  resetsOnUtc: string;
}

/** One superseded wording. `previousTexts` is the wording as it stood BEFORE that change. */
export interface DocumentTextChange {
  id: string;
  kind: DocumentTextKey;
  changeYear: number;
  changedAtUtc: string;
  previousTexts: Record<string, string>;
  changedBy: string | null;
}

export interface StoreDocumentsDto {
  /**
   * `{ [kind]: { [lang]: text } }`; a kind or a language with no text is simply absent. A named
   * contract wording's text is here too, under its key.
   */
  documentTexts: Partial<Record<DocumentTextKey, Record<string, string>>>;
  email: string | null;
  website: string | null;
  bankDetails: string | null;
  /**
   * How many times each kind's wording may be rewritten in one calendar year — the admin's own
   * setting, three by default, and sent back on the PUT so the screen that shows the allowance is
   * the screen that sets it. A change to it applies from the *next* save, never the one carrying it.
   */
  changesPerYear: number;
  /** Server-computed; present on the way back, ignored on the way in. */
  allowances?: DocumentTextAllowance[];
  /**
   * The company logo printed at the head of every document. The server keeps the file in its own
   * folder and answers a signed-in link to it (`/api/catalog/store/logo?v=…`), read through
   * SecureImageService because an `<img>` cannot send the token. On the PUT: that same link keeps
   * it, a new `data:` URL replaces it (PNG, JPEG, WebP or SVG, at most 512 KB decoded — the server
   * refuses anything else in words), null removes it. The PUT sends the whole set, so a save that
   * leaves this out clears the logo.
   */
  logoUrl?: string | null;
  /**
   * The languages every printed title is set in, in print order; empty or absent = the document
   * language alone. Sent whole on the PUT like the rest: leaving it out clears it.
   */
  titleLanguages?: string[] | null;
  /**
   * The languages an item's name prints in on the rental papers: the first is the name, a second
   * prints beneath. Empty or absent = the French name signed, with the Arabic beneath.
   */
  itemNameLanguages?: string[] | null;
  /**
   * The named rental-contract wordings, in the order the contract form lists them (PinSay #302).
   * Sent whole on the PUT: one left out is removed — refused while a contract prints it — and each
   * needs a text under its key.
   */
  contractWordings?: ContractWording[] | null;
}

/** The server's cap on the logo, decoded (StoreLogo.MaxBytes). */
export const LOGO_MAX_BYTES = 512 * 1024;

/** The types the server takes for the logo; the file picker offers these and no others. */
export const LOGO_TYPES: readonly string[] = ['image/png', 'image/jpeg', 'image/webp', 'image/svg+xml'];

@Injectable({ providedIn: 'root' })
export class StoreDocumentsApi {
  private readonly api = inject(ApiService);

  get(): Promise<StoreDocumentsDto> {
    return this.api.getJson<StoreDocumentsDto>('/api/catalog/store/documents');
  }

  /** Admin only. Sends the whole set: what is omitted is cleared, an empty text is dropped. */
  put(dto: StoreDocumentsDto): Promise<StoreDocumentsDto> {
    return this.api.putJson<StoreDocumentsDto>('/api/catalog/store/documents', dto);
  }

  /**
   * The superseded wordings, newest first — Admin only. This is what makes the copy a customer
   * signed under readable years later, so it is loaded beside the form rather than hidden behind
   * a screen of its own.
   */
  /**
   * Wording to start from, for a company whose own paragraphs do not exist yet. The server
   * hands it over; it is not stored until the admin saves it, and every sample carries a marker
   * line saying what it is — so a contract printed before anyone edited it says so on the paper.
   */
  sample(): Promise<StoreDocumentsDto> {
    return this.api.getJson<StoreDocumentsDto>('/api/catalog/store/documents/sample');
  }

  history(kind?: DocumentTextKey): Promise<DocumentTextChange[]> {
    const query = kind ? `?kind=${encodeURIComponent(kind)}` : '';
    return this.api.getJson<DocumentTextChange[]>(`/api/catalog/store/documents/history${query}`);
  }
}
