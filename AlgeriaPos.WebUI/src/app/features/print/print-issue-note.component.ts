import { Component, computed, input } from '@angular/core';
import { AdminProduct } from '../../core/admin.service';
import { RentalDay, durationOf, entitledDays, entitlement, extensionsFor, localDay, periodEndsOn } from '../../core/rentals';
import { ContractDocuments, IssueNote, RentalContractLine } from '../backoffice/rentals/rentals-api.service';
import { engineExtensions, engineLine, formatRentalDay, itemPaperName, lineName } from '../backoffice/rentals/rental-view';
import { signedByKey } from '../backoffice/rentals/rental-signatory';
import { PrintDocumentComponent } from './print-document.component';
import { LabelledLine, PrintContext, branchLine, isolate, partyLines } from './print-context';

/** One line of the note: what went out, for how long, and the day it is due back. */
interface IssueRow {
  lineId: string;
  /** The item's name in the language the company chose for its papers (answer 28). */
  name: string;
  nameLang: string;
  /** Printed beneath the name in a second chosen language; empty when none. */
  second: string;
  secondLang: string;
  secondDir: 'rtl' | 'ltr';
  sku: string;
  qty: number;
  durationDays: number;
  dueOn: RentalDay;
}

/**
 * The issue note on A4 (docs/07 §4.4): what left the yard, on whose contract, and when each
 * line is due back.
 *
 * <p>The note itself carries only quantities — the server prices documents, and a line's name,
 * its period and its due date are not on it. All three are derived here from the contract the
 * note belongs to, the same way every rental screen derives them: the line's own duration plus
 * every extension signed over it, counted from <b>this note's</b> issue day, because each batch
 * starts its own clock.</p>
 *
 * <p>It also says, in the sentence the screen uses, what this note alone invoiced: the full
 * agreed period on everything it put out. That is the rule the whole module rests on, and the
 * customer holding the paper is the person it most needs stating to.</p>
 *
 * <p>From the owner's answers of 2026-09-30: the site the goods go to (9), the driver, his phone
 * and the truck (16), each item in French with its Arabic name beneath (28), and two signature
 * boxes — the customer's and the lessor's (17). The due date counts from the first day of the
 * period as the server stored it, which is the next day for goods out after the cutoff hour.</p>
 */
@Component({
  selector: 'app-print-issue-note',
  standalone: true,
  imports: [PrintDocumentComponent],
  template: `
    @if (ctx(); as c) {
      @for (copy of copyIndexes(); track copy) {
        <app-print-sheet
          [ctx]="c"
          kind="issueNote"
          [title]="c.t('rent.issueNote')"
          titleKey="rent.issueNote"
          [number]="note().number"
          [meta]="meta()"
          [breakBefore]="copy > 0"
        >
          <div class="print-parties">
            <div class="print-block">
              <span class="print-block__title">{{ c.t('print.ctr.hirer') }}</span>
              @for (line of hirer(); track $index) {
                <div [class.print-line--lead]="!line.label">
                  @if (line.label) {
                    <span class="print-line__label">{{ line.label }}</span>
                  }
                  <span [class.mono]="!!line.label" [attr.dir]="line.ltr ? 'ltr' : null">{{ line.value }}</span>
                </div>
              }
              @if (signatory()) {
                <div>
                  <span>{{ signatory() }}</span>
                  @if (contract().signatoryIdNumber) {
                    <span class="mono"> {{ c.t('rent.sign.id', { n: isolate(contract().signatoryIdNumber!) }) }}</span>
                  }
                </div>
              }
            </div>
            <div class="print-block">
              <span class="print-block__title">{{ c.t('rent.issue.fromBranch') }}</span>
              <div>{{ branch() || '—' }}</div>
              <div>
                <span class="print-line__label">{{ c.t('rent.site.label') }}</span>
                <span>{{ contract().siteName || c.t('rent.site.notRecorded') }}</span>
              </div>
              @if (contract().siteAddress) {
                <div>{{ contract().siteAddress }}</div>
              }
              @if (note().customerTransport) {
                <div><span>{{ c.t('rent.truck.byCustomer') }}</span></div>
              } @else {
                <div>
                  <span class="print-line__label">{{ c.t('rent.truck.driverName') }}</span>
                  <span>{{ note().driverName || '—' }}</span>
                </div>
                <div>
                  <span class="print-line__label">{{ c.t('rent.truck.driverPhone') }}</span>
                  <span class="mono" dir="ltr">{{ note().driverPhone || '—' }}</span>
                </div>
                <div>
                  <span class="print-line__label">{{ c.t('rent.truck.number') }}</span>
                  <span class="mono" dir="ltr">{{ note().truckNumber || '—' }}</span>
                </div>
              }
              <div>
                <span class="print-line__label">{{ c.t('rent.raisedBy') }}</span>
                <span>{{ note().userName || '—' }}</span>
              </div>
              @if (note().note) {
                <div><span class="print-line__label">{{ c.t('rent.notes') }}</span><span>{{ note().note }}</span></div>
              }
            </div>
          </div>

          <table class="print-table">
            <thead>
              <tr>
                <th>{{ c.t('rent.item') }}</th>
                <th class="num">{{ c.t('print.iss.qtyOut') }}</th>
                <th class="num">{{ c.t('rent.entitledDays') }}</th>
                <th>{{ c.t('rent.dueOn') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (row of rows(); track row.lineId) {
                <tr>
                  <td>
                    <span [attr.lang]="row.nameLang">{{ row.name }}</span>
                    <span class="print-soft mono"> {{ row.sku }}</span>
                    @if (row.second) {
                      <br /><span [attr.dir]="row.secondDir" [attr.lang]="row.secondLang">{{ row.second }}</span>
                    }
                  </td>
                  <td class="num mono">{{ c.qty(row.qty) }}</td>
                  <td class="num mono">{{ row.durationDays }}</td>
                  <td class="mono">{{ day(row.dueOn) }}</td>
                </tr>
              }
            </tbody>
            <!-- Quantities only (owner, 2026-10-02, answer 19): the money is on the invoice. -->
            <tfoot>
              <tr class="print-total">
                <td>{{ c.t('common.total') }}</td>
                <td class="num mono">{{ c.qty(totalQty()) }}</td>
                <td></td>
                <td></td>
              </tr>
            </tfoot>
          </table>

          <!-- Answer 17: the customer signs, and so does the lessor. -->
          <div class="print-signatures">
            <div class="print-signature">{{ c.t('print.note.sign.hirer') }}</div>
            <div class="print-signature">{{ c.t('print.note.sign.lessor') }}</div>
          </div>
        </app-print-sheet>
      }
    }
  `,
})
export class PrintIssueNoteComponent {
  /** For the id number inside the "signed by" sentence: its digits keep their order on an Arabic page. */
  readonly isolate = isolate;
  readonly ctx = input.required<PrintContext>();
  /** The whole contract: the note carries quantities, everything else is derived from this. */
  readonly docs = input.required<ContractDocuments>();
  readonly note = input.required<IssueNote>();
  readonly products = input<AdminProduct[]>([]);
  readonly copies = input(1);

  readonly copyIndexes = computed(() => Array.from({ length: Math.max(1, this.copies()) }, (_, i) => i));

  readonly contract = computed(() => this.docs().contract);
  readonly customer = computed(() => this.ctx().customers.find((x) => x.id === this.contract().customerId) ?? null);
  readonly hirer = computed(() => partyLines(this.ctx(), this.customer(), { name: this.contract().customerName }));
  readonly branch = computed(() => branchLine(this.ctx(), this.note().branchId));

  readonly meta = computed<LabelledLine[]>(() => {
    const c = this.ctx();
    return [
      { label: c.t('rent.contract'), value: this.contract().number },
      { label: c.t('rent.wentOutOn'), value: c.day(this.note().issuedAtUtc) },
    ];
  });

  readonly signatory = computed(() => {
    const c = this.ctx();
    const contract = this.contract();
    const name = (contract.signatoryName ?? '').trim();
    return name ? c.t(signedByKey(contract), { name }) : '';
  });

  readonly rows = computed<IssueRow[]>(() => {
    const c = this.ctx();
    const docs = this.docs();
    const zone = c.regional.timeZone;
    const lines = docs.contract.lines;
    const engine = lines.map(engineLine);
    const extensions = engineExtensions(docs.extensions, zone);
    // The first day of the period as the server counts it; an older server sent none, and the
    // delivery day was then the rule.
    const issuedOn = this.note().periodStartsOn || localDay(this.note().issuedAtUtc, zone);
    return this.note().lines.map((noteLine) => {
      const line = lines.find((l) => l.id === noteLine.contractLineId);
      const days = line ? this.entitled(line, engine, extensions) : 0;
      const langs = c.documents?.itemNameLanguages;
      const names = line ? itemPaperName(line, this.products(), langs, c.isRtl) : null;
      return {
        lineId: noteLine.contractLineId,
        name: names?.main || (line ? lineName(line, this.products(), c.lang) : noteLine.sku),
        nameLang: langs?.[0] ?? 'fr',
        second: names?.second ?? '',
        secondLang: names?.secondLang ?? '',
        secondDir: names?.secondDir ?? 'ltr',
        sku: noteLine.sku,
        qty: noteLine.qty,
        durationDays: days,
        dueOn: periodEndsOn(issuedOn, days),
      };
    });
  });

  readonly totalQty = computed(() => this.rows().reduce((sum, r) => sum + r.qty, 0));

  /** A rental day renders as the date it says; putting it through a zone a second time moves it. */
  day(value: RentalDay): string {
    return formatRentalDay(value, this.ctx().lang);
  }

  /** The line's own duration — its parent's, for an accessory — plus every extension over it. */
  private entitled(
    line: RentalContractLine,
    engine: ReturnType<typeof engineLine>[],
    extensions: ReturnType<typeof engineExtensions>,
  ): number {
    const self = engineLine(line);
    return entitledDays(entitlement(line.rentUnitPrice, durationOf(self, engine), extensionsFor(self, extensions)));
  }
}
