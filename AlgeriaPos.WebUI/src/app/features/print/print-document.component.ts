import { Component, computed, input } from '@angular/core';
import { fillFields } from './contract-fields';
import {
  LabelledLine,
  PrintContext,
  companyLines,
  documentText,
  fiscalIdLines,
  logoSource,
  paperIssuer,
  storeName,
} from './print-context';
import { DocumentIssuer } from '../../core/document-issuer';
import { DocumentTextKey } from './store-documents-api.service';

/**
 * One printed sheet: the company's identity at the head, the document's title and number
 * beside it, the body the document component projects, and at the foot the text the owner
 * configured for this kind of paper (docs/07 §4.2).
 *
 * <p>A document that prints in several copies renders this once per copy and sets
 * `breakBefore` on every copy but the first; the page-level strip with the document number is
 * drawn by the page, not here, because a `position: fixed` element per sheet would stack.</p>
 *
 * <p>The head prints every fiscal identifier the company has a value for, with its code. The
 * profile's `showOnReceipt` flag was designed for the 80 mm slip and does not govern A4: the
 * buyer's bookkeeper needs all of them.</p>
 *
 * <p>A document raised at a branch speaks as that branch (owner, 2026-10-20): its name, address,
 * phone and identifiers, each taken from the main branch where the branch has none, then from the
 * company settings (`paperIssuer`). A paper of no branch — a statement, the ageing board, a report
 * of the whole company — prints the company's.</p>
 *
 * <p>The company logo, when one is set, stands at the inline start of the company block, about
 * 18 mm high. Without one the head is exactly what it was.</p>
 */
@Component({
  selector: 'app-print-sheet',
  standalone: true,
  template: `
    <section
      class="print-sheet"
      [class.print-sheet--a5]="a5()"
      [class.print-sheet--dense]="dense()"
      [class.print-sheet--break]="breakBefore()"
    >
      <header class="print-head">
        <div class="print-company" [class.print-company--logo]="logo()">
          @if (logo()) {
            <img class="print-logo" [src]="logo()" [attr.alt]="companyName()" />
          }
          <div class="print-company__text">
            <strong class="print-company__name">{{ companyName() }}</strong>
            @for (line of company(); track $index) {
              <div>
                @if (line.label) {
                  <span class="print-line__label">{{ line.label }}</span>
                }
                <span [attr.dir]="line.ltr ? 'ltr' : null">{{ line.value }}</span>
              </div>
            }
          </div>
          <!-- Beside the text, not inside it: under a logo the identifiers run the whole width of
               the block, so the logo narrows the name and address and never breaks a NIF in two. -->
          @if (fiscalIds().length) {
            <div class="print-ids">
              @for (id of fiscalIds(); track id.label) {
                <span><span class="print-line__label">{{ id.label }}</span><span class="mono" dir="ltr">{{ id.value }}</span></span>
              }
            </div>
          }
        </div>
        <div class="print-title">
          <h1>{{ title() }}</h1>
          <!-- The title again in the company's other title languages. The block keeps the head's
               direction so it lines up under the h1; only the words inside are isolated in their
               own, or an Arabic title on a French sheet would align to the wrong edge. -->
          @for (alt of alsoTitled(); track alt.lang) {
            <span class="print-title__alt"><bdi [attr.lang]="alt.lang" [attr.dir]="alt.dir">{{ alt.text }}</bdi></span>
          }
          @if (subtitle()) {
            <span class="print-title__sub">{{ subtitle() }}</span>
          }
          @if (numberIsText()) {
            <span class="print-title__number mono">{{ number() }}</span>
          } @else {
            <span class="print-title__number mono"><bdi dir="ltr">{{ number() }}</bdi></span>
          }
          @for (line of meta(); track $index) {
            <span class="print-title__meta">
              @if (line.label) {
                <span class="print-line__label">{{ line.label }}</span>
              }
              <span class="mono">{{ line.value }}</span>
            </span>
          }
          @if (copyLabel()) {
            <span class="print-copy">{{ copyLabel() }}</span>
          }
        </div>
      </header>

      <ng-content />

      @if (footerText() || (thanks() && receiptFooter())) {
        <footer class="print-foot">
          @if (footerText()) {
            <p>{{ footerText() }}</p>
          }
          @if (thanks() && receiptFooter()) {
            <p>{{ receiptFooter() }}</p>
          }
        </footer>
      }
      <!-- Every paper says where it ends (owner, 2026-11-09), so a missing last sheet shows. -->
      <p class="print-end">— {{ ctx().t('print.endOfDocument') }} —</p>
    </section>
  `,
})
export class PrintDocumentComponent {
  readonly ctx = input.required<PrintContext>();
  /** Who the sheet is from, when the document decides it; otherwise its branch's (`ctx.branchId`). */
  readonly issuer = input<DocumentIssuer | null>(null);
  /**
   * Which configured text prints at the foot; the owner's own wording, or nothing.
   *
   * <p>Null for a sheet that is nobody's paper — the ageing board is an internal report and the
   * owner configured no terms for it — so the foot carries only the company's receipt footer. A
   * rental contract passes the named wording it was signed under, when it names one (PinSay #302).</p>
   */
  readonly kind = input.required<DocumentTextKey | null>();
  /**
   * The values of the `{fields}` the owner may write into that text (contract-fields.ts), for a
   * document whose wording carries them — today the rental contract. Null prints the text as it
   * is stored, braces and all, which is what every other kind has always done.
   */
  readonly fields = input<Readonly<Record<string, string>> | null>(null);
  readonly title = input.required<string>();
  /** The translation key of `title`, so the head can print it in the other title languages too. */
  readonly titleKey = input<string>('');
  readonly subtitle = input<string>('');
  readonly number = input.required<string>();
  /**
   * The number is a sentence in the document language, not a code. A document number is set
   * left to right so "INV-000012" never mirrors; a report's period ("من 01/10/2026 إلى 08/10/2026")
   * is words, and isolated left to right it would read backwards on an Arabic sheet.
   */
  readonly numberIsText = input(false);
  /**
   * The company's receipt footer ("Merci de votre confiance") at the foot. A customer's paper
   * carries it; the company's own books do not — a trial balance does not thank its reader.
   */
  readonly thanks = input(true);
  /** Lines under the number: issued on, due on, received on… */
  readonly meta = input<LabelledLine[]>([]);
  readonly copyLabel = input<string>('');
  readonly breakBefore = input(false);
  readonly a5 = input(false);
  /**
   * The A4 rhythm, tightened, for a sheet that has to be exactly one page per person.
   *
   * <p>The payslip is the only document printed once per employee, so an overrun of twenty pixels
   * is a wasted sheet of paper per head on payday — the same defect the A5 voucher had. Nothing is
   * dropped and no type shrinks: the signature block and the gaps around the tables come in.</p>
   */
  readonly dense = input(false);

  readonly alsoTitled = computed(() => (this.titleKey() ? this.ctx().alsoTitled(this.titleKey()) : []));
  readonly from = computed(() => {
    const ctx = this.ctx();
    return this.issuer() ?? (ctx.branchId ? paperIssuer(ctx, ctx.branchId) : null);
  });
  readonly companyName = computed(() => this.from()?.name ?? storeName(this.ctx().store, this.ctx().lang));
  readonly company = computed(() => companyLines(this.ctx(), this.from()));
  readonly fiscalIds = computed(() => this.from()?.fiscalIds ?? fiscalIdLines(this.ctx().regional, this.ctx().store));
  readonly logo = computed(() => logoSource(this.ctx().documents));
  readonly footerText = computed(() => {
    const kind = this.kind();
    const text = kind ? documentText(this.ctx().documents, kind, this.ctx()) : '';
    const fields = this.fields();
    return fields ? fillFields(text, fields) : text;
  });
  readonly receiptFooter = computed(() => {
    const { store, lang } = this.ctx();
    if (!store) return '';
    return (lang === 'ar'
      ? store.receiptFooterAr || store.receiptFooterFr || store.receiptFooter
      : lang === 'en'
        ? store.receiptFooterEn || store.receiptFooterFr || store.receiptFooter
        : store.receiptFooterFr || store.receiptFooter
    ).trim();
  });
}
