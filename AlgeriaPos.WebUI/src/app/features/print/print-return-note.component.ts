import { Component, computed, input } from '@angular/core';
import { AdminProduct } from '../../core/admin.service';
import { ContractDocuments, ReturnNote } from '../backoffice/rentals/rentals-api.service';
import { ItemPaperName, itemPaperName, lineName } from '../backoffice/rentals/rental-view';
import { signedByKey } from '../backoffice/rentals/rental-signatory';
import { PrintDocumentComponent } from './print-document.component';
import { LabelledLine, PrintContext, branchLine, isolate, partyLines } from './print-context';

/**
 * The return note on A4 (docs/07 §4.4): what came back, in what condition, and what that costs.
 *
 * <p>Condition is five columns, not a word: sound, repairable with its repair cost, beyond
 * repair, never came back, and the day a loss was declared — the date rent stopped running on a
 * line where nothing walked back in. The charges below are the server's own stored rows, each
 * with the quantity, the days and the unit price it was worked out from, so a customer can
 * check the arithmetic rather than being handed a total.</p>
 *
 * <p>Days paid for at issue and never used are stated, and so is where they went: off an open
 * balance, or on a payment voucher when the account is clear. The internal transfer an
 * out-of-branch return causes is the company's own bookkeeping and stays off the customer's
 * copy.</p>
 *
 * <p>From the owner's answers of 2026-09-30: the driver, his phone and the truck (16), whether the
 * return day was charged and why when the manager decided (6), each item in French with its
 * Arabic name beneath (28), and two signature boxes — the customer's and the lessor's (17).</p>
 */
@Component({
  selector: 'app-print-return-note',
  standalone: true,
  imports: [PrintDocumentComponent],
  // The column heads are long words ("prix de casse totale"); kept on one line they pushed the
  // table past the sheet. The figures beneath stay unbroken.
  styles: [
    `
      .print-table thead th.num {
        white-space: normal;
      }
    `,
  ],
  template: `
    @if (ctx(); as c) {
      @for (copy of copyIndexes(); track copy) {
        <app-print-sheet
          [ctx]="c"
          kind="returnNote"
          [title]="c.t('rent.returnNote')"
          titleKey="rent.returnNote"
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
              <span class="print-block__title">{{ c.t('rent.return.takenAt') }}</span>
              <div>{{ branch() || '—' }}</div>
              <!-- Taken back somewhere other than where it went out: the customer's copy says so,
                   because the two branches are two different yards to him as well. -->
              @if (issuedElsewhere()) {
                <div>
                  <span class="print-line__label">{{ c.t('rent.issue.fromBranch') }}</span>
                  <span>{{ contractBranch() }}</span>
                </div>
              }
              <!-- The site the goods came back from, as the issue note names the one they went to. -->
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
                <span class="print-line__label">{{ c.t('rent.day.returnTitle') }}</span>
                <span>{{ c.t(note().countReturnDay ? 'rent.day.counted' : 'rent.day.notCounted') }}</span>
                @if (note().returnDayReason) {
                  <span> — {{ note().returnDayReason }}</span>
                }
              </div>
              <!-- Back late and asked (owner, 2026-10-08): the answer, why, and who gave it; a
                   deduction (2026-10-22) says how much, in days or against the late total. -->
              @if (note().lateDecidedBy) {
                <div>
                  <span class="print-line__label">{{ c.t('rent.late.print') }}</span>
                  <span>{{ lateAnswer(c) }}</span>
                  @if (note().lateWaiverReason) {
                    <span> — {{ note().lateWaiverReason }}</span>
                  }
                  <span> ({{ note().lateDecidedBy }})</span>
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
                <th class="num">{{ c.t('rent.return.sound') }}</th>
                <th class="num">{{ c.t('rent.return.repairable') }}</th>
                <th class="num">{{ c.t('rent.return.beyondRepair') }}</th>
                <th class="num">{{ c.t('rent.return.neverCameBack') }}</th>
                <th>{{ c.t('rent.return.reportedOn') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (line of note().lines; track line.contractLineId) {
                <tr>
                  <td>
                    @if (names(line.contractLineId); as n) {
                      <span [attr.lang]="nameLang()">{{ n.main }}</span>
                    } @else {
                      <span>—</span>
                    }
                    <span class="print-soft mono"> {{ line.sku }}</span>
                    @if (line.sameDayUsage === 'Used' || line.sameDayUsage === 'NotUsed') {
                      <br /><span>{{ c.t('rent.usage.state') }}: {{ c.t(line.sameDayUsage === 'Used' ? 'rent.usage.used' : 'rent.usage.notUsed') }}</span>
                    }
                    @if (names(line.contractLineId)?.second) {
                      <br /><span [attr.dir]="names(line.contractLineId)!.secondDir" [attr.lang]="names(line.contractLineId)!.secondLang">{{ names(line.contractLineId)!.second }}</span>
                    }
                  </td>
                  <td class="num mono">{{ c.qty(line.qtyOk) }}</td>
                  <td class="num mono">{{ c.qty(line.qtyPartialDamage) }}</td>
                  <td class="num mono">{{ c.qty(line.qtyTotalDamage) }}</td>
                  <td class="num mono">{{ c.qty(line.qtyMissing) }}</td>
                  <td class="mono">{{ line.reportedOnUtc ? c.day(line.reportedOnUtc) : '' }}</td>
                </tr>
              }
            </tbody>
          </table>

          <!-- Quantities only (owner, 2026-10-02, answer 19): what a repair or a loss costs is
               paid on a receipt voucher that names this contract, never on the note. -->

          @if (note().contractClosed) {
            <div class="print-notice">
              <strong>{{ c.t('rent.return.closesContract') }}</strong>
            </div>
          }

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
export class PrintReturnNoteComponent {
  /** For the id number inside the "signed by" sentence: its digits keep their order on an Arabic page. */
  readonly isolate = isolate;
  readonly ctx = input.required<PrintContext>();
  /** The whole contract: a note line names a contract line, never the item. */
  readonly docs = input.required<ContractDocuments>();
  readonly note = input.required<ReturnNote>();
  readonly products = input<AdminProduct[]>([]);
  readonly copies = input(1);

  readonly copyIndexes = computed(() => Array.from({ length: Math.max(1, this.copies()) }, (_, i) => i));

  readonly contract = computed(() => this.docs().contract);
  readonly customer = computed(() => this.ctx().customers.find((x) => x.id === this.contract().customerId) ?? null);
  readonly hirer = computed(() => partyLines(this.ctx(), this.customer(), { name: this.contract().customerName }));
  readonly branch = computed(() => branchLine(this.ctx(), this.note().branchId));
  readonly contractBranch = computed(() => branchLine(this.ctx(), this.contract().branchId));
  readonly issuedElsewhere = computed(() => this.note().branchId !== this.contract().branchId && !!this.contractBranch());

  readonly meta = computed<LabelledLine[]>(() => {
    const c = this.ctx();
    return [
      { label: c.t('rent.contract'), value: this.contract().number },
      { label: c.t('rent.returnedOn'), value: c.day(this.note().returnedAtUtc) },
    ];
  });

  readonly signatory = computed(() => {
    const c = this.ctx();
    const contract = this.contract();
    const name = (contract.signatoryName ?? '').trim();
    return name ? c.t(signedByKey(contract), { name }) : '';
  });

  /** The answer to "charge the late days?" in words: charged, waived, or charged less a deduction. */
  lateAnswer(c: PrintContext): string {
    const note = this.note();
    if (note.waiveOverdue) return c.t('rent.late.waived');
    if (note.lateDaysWaived != null) return c.t('rent.late.deductedDays', { n: isolate(String(note.lateDaysWaived)) });
    if (note.lateAmountWaived != null) {
      return c.t('rent.late.deductedAmount', {
        v: isolate(c.money(note.lateAmountWaived)),
        full: isolate(c.money(note.lateAmountFull ?? 0)),
      });
    }
    return c.t('rent.late.charged');
  }

  /** The item's names as the company's setting asks (answer 28); null for a line not on the contract. */
  names(contractLineId: string): ItemPaperName | null {
    const line = this.contract().lines.find((l) => l.id === contractLineId);
    if (!line) return null;
    const names = itemPaperName(line, this.products(), this.ctx().documents?.itemNameLanguages, this.ctx().isRtl);
    return { ...names, main: names.main || lineName(line, this.products(), this.ctx().lang) };
  }

  /** The language the name is in, for the `lang` attribute: the first chosen, French when unset. */
  nameLang(): string {
    return this.ctx().documents?.itemNameLanguages?.[0] ?? 'fr';
  }
}
