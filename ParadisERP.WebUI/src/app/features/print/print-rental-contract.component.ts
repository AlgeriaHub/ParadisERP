import { Component, computed, input } from '@angular/core';
import { AdminProduct } from '../../core/admin.service';
import { RentalDay, addDays, durationOf, localDay, rentalAmount } from '../../core/rentals';
import { IssueNote, RentalContract, RentalContractLine } from '../backoffice/rentals/rentals-api.service';
import { engineLine, formatRentalDay, itemPaperName, lineName } from '../backoffice/rentals/rental-view';
import { signedByKey } from '../backoffice/rentals/rental-signatory';
import { ContractPeriod, ContractTransport, contractFieldValues, fillFields, splitAtItems } from './contract-fields';
import { PrintDocumentComponent } from './print-document.component';
import { LabelledLine, PrintContext, branchLine, paperIssuer, documentText, isolate, partyLines, utc } from './print-context';
import { DocumentTextKey } from './store-documents-api.service';

/** One contract line ready to print: the names that were signed, its period. */
interface ContractRow {
  line: RentalContractLine;
  /** The item's name in the language the company chose for its papers (answer 28). */
  name: string;
  nameLang: string;
  /** Printed beneath the name in a second chosen language; empty when none. */
  second: string;
  secondLang: string;
  secondDir: 'rtl' | 'ltr';
  isAccessory: boolean;
  /** The parent's name, so an accessory says whose period it inherits. */
  parentName: string;
  durationDays: number;
  periodRent: number;
  /**
   * The line's period as the company's own contract prints it ("تاريخ من … إلى"): from the day its
   * first issue note starts the clock, or — not issued yet — the day it was signed, to that day
   * plus the agreed days.
   */
  fromDay: RentalDay;
  toDay: RentalDay;
}

/**
 * The rental contract on A4, one copy by default and two on request (docs/07 §4.4). This is the paper §5.5's dispute is
 * about: the signed overdue price the customer never saw.
 *
 * <p>Two unit prices are on every line — hire and total damage. The overdue price stays on the
 * screens and off the paper: the owner's answer 7c (2026-09-30) is that it is not printed on the
 * contract. The item is named in French, as it was signed, with its Arabic name beneath it
 * whatever the language of the print (answer 28); an item with no Arabic name prints the French
 * alone. An accessory sits under its parent and says whose period it inherits.</p>
 *
 * <p>The signatory block carries the whole identity the owner's paper records (answer 10): the
 * document, its number, who issued it and when, and the date of birth. The site the goods go to is
 * printed as "Chantier" (answer 9), "not recorded" on a contract raised before the site rule, and
 * the lorry when one is named (answer 12).</p>
 *
 * <p>The rules the system can state are stated — the period starts at issue, a rental month is
 * thirty days, the full period is charged at issue, and an overdue day is charged at the overdue
 * price signed on the line — and nothing else: the legal terms are the owner's own text from
 * `Store.DocumentTexts`, which the frame prints at the foot. The code writes no terms. The text may
 * name fields — `{customer}`, `{deposit}`… (contract-fields.ts) — and they print as this contract's
 * own values, in the document language.</p>
 *
 * <p>The overdue rule names no multiplier, deliberately. The company-wide figure is not what
 * bound this contract: a product can override it (`Product.RentalOverdueMultiplier`) and a
 * manager can sign a figure above the floor, and the company figure moves afterwards. Printing
 * today's multiplier under prices signed months ago put two contradicting figures on the one
 * sheet the dispute is about. The signed overdue price is already on every line, which was the
 * whole point of putting it there.</p>
 *
 * <p>No draft watermark, whatever the lock says: a contract is signed before it is issued, and
 * lock state belongs on the screen, not on the customer's copy.</p>
 */
@Component({
  selector: 'app-print-rental-contract',
  standalone: true,
  imports: [PrintDocumentComponent],
  // The column heads are long words ("prix de casse totale"); kept on one line they pushed the
  // table past the sheet. The figures beneath stay unbroken.
  styles: [
    `
      .print-table thead th.num {
        white-space: normal;
      }
      /* The company's own words, as it typed them: its line breaks are its clauses. */
      .print-wording {
        white-space: pre-line;
        font-size: var(--fs-base);
        /* Tight enough that long terms (14 articles) end with their signatures on the second sheet (owner, 2026-11-09). */
        line-height: 1.55;
        margin-block: var(--sp-3);
        /* With the signatures' break-before: avoid, at least three lines of the terms go over with them. */
        orphans: 3;
        widows: 3;
      }
    `,
  ],
  template: `
    @if (ctx(); as c) {
      @if (ownPaper(); as paper) {
        <!-- The company's own contract (owner, 2026-10-10, contract 12547): the wording is the body,
             and the items marker is where the table stands. Nothing of the standard layout is added — the
             wording says who the parties are and what the rules are, in the company's words. -->
        @for (copy of copyIndexes(); track copy) {
          <app-print-sheet
            [ctx]="c"
            [issuer]="issuer()"
            [kind]="null"
            [thanks]="false"
            [title]="c.t('print.ctr.title')"
            titleKey="print.ctr.title"
            [number]="contract().number"
            [copyLabel]="copyLabel(copy)"
            [breakBefore]="copy > 0"
          >
            @if (paper.before) {
              <div class="print-wording">{{ paper.before }}</div>
            }
            <table class="print-table">
              <thead>
                <tr>
                  <th>{{ c.t('print.ctr.col.item') }}</th>
                  <th class="num">{{ c.t('print.ctr.col.qty') }}</th>
                  <th class="num">{{ c.t('print.ctr.col.dayPrice') }}</th>
                  <th class="num">{{ c.t('print.ctr.col.from') }}</th>
                  <th class="num">{{ c.t('print.ctr.col.to') }}</th>
                  <th class="num">{{ c.t('print.ctr.col.days') }}</th>
                  @if (taxed()) {
                    <th>{{ c.t('receipt.rate') }}</th>
                  }
                  <th class="num">{{ c.t('print.ctr.col.total') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (row of rows(); track row.line.id) {
                  <tr>
                    <td>
                      @if (row.isAccessory) {
                        <span class="print-indent" aria-hidden="true"></span>
                      }
                      <span [attr.lang]="row.nameLang">{{ row.name }}</span>
                      @if (row.second) {
                        <br /><span [attr.dir]="row.secondDir" [attr.lang]="row.secondLang">{{ row.second }}</span>
                      }
                    </td>
                    <td class="num mono">{{ c.qty(row.line.qty) }}</td>
                    <td class="num mono">{{ c.money(row.line.rentUnitPrice) }}</td>
                    <td class="num mono">{{ day(row.fromDay) }}</td>
                    <td class="num mono">{{ day(row.toDay) }}</td>
                    <td class="num mono">{{ row.durationDays }}</td>
                    @if (taxed()) {
                      <td>{{ rateWord(row.line.taxRate) }}</td>
                    }
                    <td class="num mono">{{ c.money(row.periodRent) }}</td>
                  </tr>
                }
              </tbody>
              <tfoot>
                <tr class="print-total">
                  <td [attr.colspan]="taxed() ? 7 : 6">{{ c.t('print.ctr.total') }}</td>
                  <td class="num mono">{{ c.money(periodRentTotal()) }}</td>
                </tr>
              </tfoot>
            </table>
            @if (paper.after) {
              <div class="print-wording">{{ paper.after }}</div>
            }
            <div class="print-signatures">
              <div class="print-signature">{{ c.t('print.ctr.firstParty') }}</div>
              <div class="print-signature">{{ c.t('print.ctr.secondParty') }}</div>
            </div>
          </app-print-sheet>
        }
      } @else {
      @for (copy of copyIndexes(); track copy) {
        <app-print-sheet
          [ctx]="c"
          [issuer]="issuer()"
          [kind]="wording()"
          [fields]="fields()"
          [title]="c.t('print.ctr.title')"
          titleKey="print.ctr.title"
          [number]="contract().number"
          [meta]="meta()"
          [copyLabel]="copyLabel(copy)"
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
                @for (line of identity(); track line.label) {
                  <div>
                    <span class="print-line__label">{{ line.label }}</span>
                    <span>{{ line.value }}</span>
                  </div>
                }
              }
            </div>
            <div class="print-block">
              <span class="print-block__title">{{ c.t('print.ctr.place') }}</span>
              <div>{{ branch() || '—' }}</div>
              <div>
                <span class="print-line__label">{{ c.t('rent.site.label') }}</span>
                <span>{{ contract().siteName || c.t('rent.site.notRecorded') }}</span>
              </div>
              @if (contract().siteAddress) {
                <div>{{ contract().siteAddress }}</div>
              }
              @if (contract().notes) {
                <div><span class="print-line__label">{{ c.t('rent.notes') }}</span><span>{{ contract().notes }}</span></div>
              }
            </div>
          </div>

          <table class="print-table">
            <thead>
              <tr>
                <th>{{ c.t('rent.item') }}</th>
                <th class="num">{{ c.t('register.qty') }}</th>
                <th class="num">{{ c.t('rent.durationDays') }}</th>
                <th class="num">{{ c.t('rent.hirePrice') }}</th>
                <th class="num">{{ c.t('rent.damageTotal') }}</th>
                @if (taxed()) {
                  <th>{{ c.t('receipt.rate') }}</th>
                }
                <th class="num">{{ c.t('print.ctr.periodRent') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (row of rows(); track row.line.id) {
                <tr>
                  <td>
                    @if (row.isAccessory) {
                      <span class="print-indent" aria-hidden="true"></span>
                    }
                    <span [attr.lang]="row.nameLang">{{ row.name }}</span>
                    <span class="print-soft mono"> {{ row.line.sku }}</span>
                    @if (row.isAccessory) {
                      <span class="print-soft"> {{ c.t('print.ctr.inheritsPeriod', { of: row.parentName }) }}</span>
                    }
                    @if (row.second) {
                      <br /><span [attr.dir]="row.secondDir" [attr.lang]="row.secondLang">{{ row.second }}</span>
                    }
                  </td>
                  <td class="num mono">{{ c.qty(row.line.qty) }}</td>
                  <td class="num mono">{{ row.durationDays }}</td>
                  <td class="num mono">{{ c.money(row.line.rentUnitPrice) }}</td>
                  <td class="num mono">{{ c.money(row.line.qty * row.line.damageUnitPrice) }}</td>
                  @if (taxed()) {
                    <td>{{ rateWord(row.line.taxRate) }}</td>
                  }
                  <td class="num mono">{{ c.money(row.periodRent) }}</td>
                </tr>
              }
            </tbody>
            <tfoot>
              <tr class="print-total">
                <td>{{ c.t('print.ctr.periodRentTotal') }}</td>
                <td></td>
                <td></td>
                <td></td>
                <td></td>
                @if (taxed()) {
                  <td></td>
                }
                <td class="num mono">{{ c.money(periodRentTotal()) }}</td>
              </tr>
            </tfoot>
          </table>

          <p class="print-sentence">{{ c.t('print.ctr.periodRentHint') }}</p>

          <!-- A zero deposit prints nothing at all, switch on or off: a line saying 0.00 reads
               as a deposit that was taken and lost. -->
          @if (deposit() > 0) {
            <table class="print-table print-table--compact">
              <tbody>
                <tr class="print-total">
                  <td>{{ c.t('rent.deposit') }}</td>
                  <td class="num mono">{{ c.money(deposit()) }}</td>
                </tr>
              </tbody>
            </table>
          }

          <div class="print-notice">
            <strong>{{ c.t('print.ctr.rules') }}</strong>
            <p>{{ c.t('rent.issue.clockHint') }}</p>
            <p>{{ c.t('print.ctr.rule.daily') }}</p>
            <p>{{ c.t('rent.issue.fullPeriodRule') }}</p>
            <p>{{ c.t('print.ctr.rule.overdueDays') }}</p>
          </div>

          <div class="print-signatures">
            <div class="print-signature">{{ c.t('print.ctr.forCompany') }}</div>
            <div class="print-signature">{{ c.t('print.ctr.forHirer') }}</div>
          </div>
        </app-print-sheet>
      }
      }
    }
  `,
})
export class PrintRentalContractComponent {
  /** For the id number inside the "signed by" sentence: its digits keep their order on an Arabic page. */
  readonly isolate = isolate;
  readonly ctx = input.required<PrintContext>();
  readonly contract = input.required<RentalContract>();
  readonly issuer = computed(() => paperIssuer(this.ctx(), this.contract().branchId));
  readonly products = input<AdminProduct[]>([]);
  /** The contract's issue notes, oldest first or not: each line's period starts at the first one that put it out. */
  readonly issueNotes = input<IssueNote[]>([]);
  /** One by default (owner, 2026-10-10); two labels them the company's copy and the hirer's. */
  readonly copies = input(1);

  readonly copyIndexes = computed(() => Array.from({ length: Math.max(1, this.copies()) }, (_, i) => i));

  readonly customer = computed(() => this.ctx().customers.find((x) => x.id === this.contract().customerId) ?? null);
  readonly hirer = computed(() => partyLines(this.ctx(), this.customer(), { name: this.contract().customerName }));
  readonly branch = computed(() => branchLine(this.ctx(), this.contract().branchId));

  /** Null while the deposit switch is off, which reads as zero and prints nothing. */
  readonly deposit = computed(() => this.contract().depositAmount ?? 0);
  /**
   * The terms this contract prints (owner, PinSay #302): the named wording it was signed under, or
   * the standard one. The server keeps a named wording with a text and refuses to remove one a
   * contract names, so the fallback is only for a server older than the choice.
   */
  readonly wording = computed<DocumentTextKey>(() => {
    const key = this.contract().wordingKey;
    return key && this.ctx().documents?.documentTexts?.[key as DocumentTextKey] ? (key as DocumentTextKey) : 'rentalContract';
  });

  /** Untaxed hire is the default here; a rate column of zeros would say nothing. */
  readonly taxed = computed(() => this.contract().lines.some((l) => l.taxRate > 0));

  readonly meta = computed<LabelledLine[]>(() => {
    const c = this.ctx();
    return [{ label: c.t('rent.signedOn'), value: c.day(this.contract().signedAtUtc) }];
  });

  /**
   * Who signed, from the frozen snapshot on the contract — never the customer's current
   * representative, who may be someone else by now. Blank on a contract raised before the rule.
   */
  readonly signatory = computed(() => {
    const c = this.ctx();
    const contract = this.contract();
    const name = (contract.signatoryName ?? '').trim();
    return name ? c.t(signedByKey(contract), { name }) : '';
  });

  /** The rest of the signatory's identity, each part labelled; a part not recorded is not printed. */
  readonly identity = computed<LabelledLine[]>(() => {
    const c = this.ctx();
    const contract = this.contract();
    const lines: LabelledLine[] = [];
    if (contract.signatoryIdType) lines.push({ label: c.t('rent.id.type'), value: c.t('rent.id.kind.' + contract.signatoryIdType) });
    if (contract.signatoryIdIssuer) lines.push({ label: c.t('rent.id.issuer'), value: contract.signatoryIdIssuer });
    if (contract.signatoryIdIssuedOn) {
      lines.push({ label: c.t('rent.id.issuedOn'), value: formatRentalDay(contract.signatoryIdIssuedOn, c.lang) });
    }
    if (contract.signatoryBirthDate) {
      lines.push({ label: c.t('rent.id.birthDate'), value: formatRentalDay(contract.signatoryBirthDate, c.lang) });
    }
    return lines;
  });

  /** Parents in their contracted order, each followed by the accessories hanging off it. */
  readonly rows = computed<ContractRow[]>(() => {
    const c = this.ctx();
    const contract = this.contract();
    const lines = contract.lines;
    const engine = lines.map(engineLine);
    const signedOn = contract.signedAtUtc ? localDay(utc(contract.signedAtUtc), c.regional.timeZone) : '';
    const row = (line: RentalContractLine): ContractRow => {
      const langs = c.documents?.itemNameLanguages;
      const names = itemPaperName(line, this.products(), langs, c.isRtl);
      const days = durationOf(engineLine(line), engine);
      const parent = lines.find((l) => l.id === line.parentLineId);
      // An accessory runs on its parent's clock.
      const fromDay = this.startsOn(parent?.id ?? line.id) ?? signedOn;
      return {
        line,
        name: names.main || lineName(line, this.products(), c.lang),
        nameLang: langs?.[0] ?? 'fr',
        second: names.second,
        secondLang: names.secondLang,
        secondDir: names.secondDir,
        isAccessory: !!line.parentLineId,
        parentName: parent ? itemPaperName(parent, this.products(), langs, c.isRtl).main || lineName(parent, this.products(), c.lang) : '',
        durationDays: days,
        periodRent: line.rentalTotal ?? rentalAmount(line.rentUnitPrice, days, line.qty, c.regional.currencyDecimals),
        fromDay,
        toDay: fromDay ? addDays(fromDay, days) : '',
      };
    };
    const out: ContractRow[] = [];
    for (const line of lines.filter((l) => !l.parentLineId)) {
      out.push(row(line));
      for (const accessory of lines.filter((l) => l.parentLineId === line.id)) out.push(row(accessory));
    }
    // An accessory whose parent is not on this contract would otherwise vanish from the paper.
    for (const line of lines.filter((l) => l.parentLineId && !lines.some((p) => p.id === l.parentLineId))) {
      out.push(row(line));
    }
    return out;
  });

  readonly periodRentTotal = computed(() => this.contract().rentalTotal ?? this.rows().reduce((sum, r) => sum + r.periodRent, 0));

  /** The whole contract's period and what its goods are worth: the first day to the last, the longest agreement. */
  readonly period = computed<ContractPeriod>(() => {
    const rows = this.rows();
    const from = rows.map((r) => r.fromDay).filter((d) => !!d).sort();
    const to = rows.map((r) => r.toDay).filter((d) => !!d).sort();
    const days = rows.map((r) => r.durationDays);
    return {
      startDay: from[0] ?? null,
      endDay: to[to.length - 1] ?? null,
      durationDays: days.length ? Math.max(...days) : null,
      damageTotal: this.contract().lines.reduce((sum, l) => sum + l.qty * l.damageUnitPrice, 0),
    };
  });

  /** What the wording's `{fields}` print as on this contract; the rent total is the table's own. */
  readonly fields = computed(() =>
    contractFieldValues(this.ctx(), this.contract(), this.customer(), this.periodRentTotal(), this.period(), this.transport()),
  );

  /**
   * The lorry of the first issue note that went out in one — the driver, his phone, the truck — for
   * the wording's transport line. A carry-over moved nothing, and goods the customer carried away
   * himself had no lorry, so neither is read.
   */
  readonly transport = computed<ContractTransport | null>(() => {
    const note = [...this.issueNotes()]
      .filter((n) => !n.isCarryOver && !n.customerTransport && (n.truckNumber || n.driverName || n.driverPhone))
      .sort((a, b) => a.issuedAtUtc.localeCompare(b.issuedAtUtc))[0];
    if (!note) return null;
    return {
      truckNumber: note.truckNumber?.trim() || null,
      driverName: note.driverName?.trim() || null,
      driverPhone: note.driverPhone?.trim() || null,
    };
  });

  /**
   * The wording split at `{items}`, filled in: the company's own contract. Null when the wording
   * carries no table, and then the standard layout prints it as terms at the foot.
   */
  readonly ownPaper = computed(() => {
    const text = documentText(this.ctx().documents, this.wording(), this.ctx());
    const split = splitAtItems(text);
    if (!split) return null;
    const fields = this.fields();
    return { before: fillFields(split.before, fields), after: fillFields(split.after, fields) };
  });

  /** The first day of a line's period: the first issue note that put it out, as the server counts it. */
  private startsOn(lineId: string): RentalDay | null {
    const zone = this.ctx().regional.timeZone;
    const days = this.issueNotes()
      .filter((n) => n.lines.some((l) => l.contractLineId === lineId && l.qty > 0))
      .map((n) => n.periodStartsOn || localDay(utc(n.issuedAtUtc), zone))
      .sort();
    return days[0] ?? null;
  }

  day(value: RentalDay): string {
    return value ? formatRentalDay(value, this.ctx().lang) : '—';
  }

  /** Company copy first, hirer's copy second; a third and later copy is unlabelled. */
  copyLabel(index: number): string {
    const c = this.ctx();
    if (this.copies() < 2) return '';
    return index === 0 ? c.t('print.rcpt.copy.company') : index === 1 ? c.t('print.rcpt.copy.customer') : '';
  }

  /** "19 % Taux normal", or just the figure when the profile names no label. */
  rateWord(rate: number): string {
    const label = this.ctx().rateLabel(rate);
    return label ? `${rate} % ${label}` : `${rate} %`;
  }
}
