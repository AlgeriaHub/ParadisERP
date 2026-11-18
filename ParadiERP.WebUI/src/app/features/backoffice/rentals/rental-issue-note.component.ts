import { Component, OnInit, computed, effect, inject, input, output, signal } from '@angular/core';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest } from '../../../ui/confirm-requests';
import { FoldComponent } from '../../../ui/fold.component';
import { DateInputComponent } from '../../../ui/date-input.component';
import { PageChrome } from '../shell/page-chrome';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminProduct, adminErrorMessage } from '../../../core/admin.service';
import { BranchLock } from '../../../core/branch-lock';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import {
  durationOf,
  entitledDays,
  entitlement,
  extensionsFor,
  issueProblem,
  localTime,
  periodEndsOn,
  periodStartsOn,
  rentalAmount,
  transportMissing,
  zonedTimeToUtcIso,
} from '../../../core/rentals';
import { localizedName } from '../backoffice-format';
import { TouchedFields } from '../backoffice-forms';
import { ContractDocuments, IssueNote, RentalContractLine, RentalSettings, RentalsApi } from './rentals-api.service';
import { identityMissing, signatoryState, signedByKey } from './rental-signatory';
import { engineExtensions, engineLine, formatRentalDay, lineName, nowTime, today } from './rental-view';

/**
 * Goods leaving for the site — and the one act on this contract that cannot be undone.
 *
 * <p>Three things happen the moment it is saved and none of them can happen without the others:
 * the quantity moves from available to on rent, the customer is invoiced the **full agreed
 * period** on everything that went out, and — if this is the first note — the contract locks
 * against editing for good. After it, the prices and quantities the customer signed can only be
 * changed by raising another document.</p>
 *
 * <p>So the lock is stated before it happens and has to be acknowledged, rather than being
 * discovered afterwards by a clerk who wanted to fix a typo in a price.</p>
 *
 * <p>The lock is also where the paper has to say who signed it. The server refuses the first note
 * on a contract raised under the signatory rule while its name is blank; this screen says so in
 * the reader's words before the request goes out, and points back at the contract, where Edit is
 * one tap away. Only the first note: once locked, the snapshot is frozen with everything else.
 * Since the owner's answer 10 that means the whole identity — document type, number, issuer,
 * issue date and birth date — and every missing part is named.</p>
 *
 * <p>The driver, his phone and the truck are mandatory (answer 16: "yes, it refuses to save").
 * The truck starts from the one named on the contract and is the note's own; a customer who
 * collects with his own vehicle has his truck's number written instead. The hour the goods leave
 * matters once the company sets a day cutoff (answer 5): after it, the period starts tomorrow, and
 * the due date below says so.</p>
 */
@Component({
  selector: 'app-rental-issue-note',
  standalone: true,
  imports: [FoldComponent, FormsModule, DateInputComponent],
  template: `
    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      @if (docs(); as d) {
        <!-- The title is the page header's (Rentals › Contracts › CTR-… › New issue note); the
             one Cancel is in the action bar below, which stays on screen. -->
        <section class="bo-panel form-panel">
          <div class="bo-panel__head">
            <p class="bo-meta">
              <span class="mono">{{ d.contract.number }}</span>
              <span>{{ d.contract.customerName || '—' }}</span>
              <span class="bo-soft">{{ i18n.t('rent.site.label') }}</span>
              <span>{{ d.contract.siteName || i18n.t('rent.site.notRecorded') }}</span>
              <!-- Whose name goes on the paper that is about to lock. -->
              @switch (signatory()) {
                @case ('signed') {
                  <span>
                    {{ i18n.t(signedByKey(d.contract), { name: d.contract.signatoryName! }) }}
                    @if (d.contract.signatoryIdNumber) {
                      <span class="mono">{{ i18n.t('rent.sign.id', { n: isolate(d.contract.signatoryIdNumber) }) }}</span>
                    } @else {
                      <span class="bo-soft">{{ i18n.t('rent.sign.idMissing') }}</span>
                    }
                  </span>
                }
                @case ('predates') {
                  <span class="bo-soft">{{ i18n.t('rent.sign.predates') }}</span>
                }
                @default {
                  <span class="bo-state bo-state--warn">{{ i18n.t('rent.sign.missing') }}</span>
                }
              }
            </p>
          </div>

          <div class="bo-panel__body bo-form">
            <app-fold class="bo-group" [heading]="i18n.t('rent.issue.when')" [open]="true">
              <p class="bo-group__note">{{ i18n.t('rent.issue.clockHint') }}</p>
              <p class="bo-group__note">{{ i18n.t('rent.issue.fromBranchHint') }}</p>
              <div class="bo-fields bo-fields--roomy">
                @if (lock.locked()) {
                  <div class="bo-field">
                    <p class="bo-fixed-label">{{ i18n.t('rent.issue.fromBranch') }}</p>
                    <p class="bo-fixed" id="ri-fixed">{{ lock.ownBranchName(branches()) }}</p>
                    <span class="bo-hint">{{ i18n.t('rent.issue.fromOwnHint') }}</span>
                  </div>
                } @else {
                  <div class="bo-field">
                    <label for="ri-branch">
                      {{ i18n.t('rent.issue.fromBranch') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                    </label>
                    <select id="ri-branch" [ngModel]="branchId()" (ngModelChange)="branchId.set($event)">
                      @for (b of branches(); track b.id) {
                        <option [ngValue]="b.id">{{ branchName(b) }}</option>
                      }
                    </select>
                  </div>
                }
                <div class="bo-field">
                  <label for="ri-date">{{ i18n.t('rent.wentOutOn') }}</label>
                  <app-date-input id="ri-date" [ngModel]="issuedOn()" (ngModelChange)="issuedOn.set($event)" />
                </div>
                <div class="bo-field">
                  <label for="ri-time">{{ i18n.t('rent.day.issueTime') }}</label>
                  <input id="ri-time" type="time" class="mono" [ngModel]="issuedTime()" (ngModelChange)="issuedTime.set($event)" />
                </div>
                <div class="bo-field bo-field--wide">
                  <label for="ri-note">
                    {{ i18n.t('rent.notes') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                  </label>
                  <input id="ri-note" maxlength="500" [ngModel]="note()" (ngModelChange)="note.set($event)" />
                </div>
              </div>
              @if (startsNextDay()) {
                <p class="bo-hint">
                  <span class="bo-state bo-state--lead">{{ i18n.t('rent.day.afterCutoff') }}</span>
                  {{ i18n.t('rent.day.startsOn', { h: cutoffLabel(), day: rentalDay(startDay()) }) }}
                </p>
              }
            </app-fold>

            <!-- Open: the driver and the truck are required whenever the branch transports. -->
            <app-fold class="bo-group" [heading]="i18n.t('rent.truck.noteTitle')" [open]="true">
              <p class="bo-group__note">{{ i18n.t('rent.truck.ownVehicle') }}</p>
              <!-- Owner, 2026-10-02: the driver and the truck are mandatory when the branch
                   transports; when the customer carries the goods the note says so and asks none. -->
              <div class="bo-check">
                <input
                  id="ri-by-customer"
                  type="checkbox"
                  [ngModel]="customerTransport()"
                  (ngModelChange)="customerTransport.set($event)"
                />
                <label for="ri-by-customer">{{ i18n.t('rent.truck.byCustomer') }}</label>
              </div>
              @if (!customerTransport()) {
              <div class="bo-fields bo-fields--roomy">
                <div class="bo-field" [class.is-invalid]="transportInvalid('driverName')">
                  <label for="ri-driver">
                    {{ i18n.t('rent.truck.driverName') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <input
                    id="ri-driver"
                    maxlength="120"
                    [ngModel]="driverName()"
                    (ngModelChange)="driverName.set($event)"
                    (blur)="touched.touch('driverName')"
                  />
                  @if (transportInvalid('driverName')) {
                    <span class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                  }
                </div>
                <div class="bo-field" [class.is-invalid]="transportInvalid('driverPhone')">
                  <label for="ri-driver-phone">
                    {{ i18n.t('rent.truck.driverPhone') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <input
                    id="ri-driver-phone"
                    dir="ltr"
                    class="mono"
                    maxlength="40"
                    [ngModel]="driverPhone()"
                    (ngModelChange)="driverPhone.set($event)"
                    (blur)="touched.touch('driverPhone')"
                  />
                  @if (transportInvalid('driverPhone')) {
                    <span class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                  }
                </div>
                <div class="bo-field" [class.is-invalid]="transportInvalid('truckNumber')">
                  <label for="ri-truck">
                    {{ i18n.t('rent.truck.number') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <input
                    id="ri-truck"
                    dir="ltr"
                    class="mono"
                    maxlength="40"
                    [ngModel]="truckNumber()"
                    (ngModelChange)="truckNumber.set($event)"
                    (blur)="touched.touch('truckNumber')"
                  />
                  @if (transportInvalid('truckNumber')) {
                    <span class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                  }
                </div>
              </div>
              }
            </app-fold>
          </div>

          <div class="bo-scroll">
            <table class="bo-data">
              <thead>
                <tr>
                  <th>{{ i18n.t('rent.item') }}</th>
                  <th class="num">{{ i18n.t('rent.contracted') }}</th>
                  <th class="num">{{ i18n.t('rent.issued') }}</th>
                  <th class="num">{{ i18n.t('rent.leftToIssue') }}</th>
                  <th class="num">
                    {{ i18n.t('rent.issue.sendNow') }}
                    @if (issuable().length > 1) {
                      <button type="button" class="btn-ghost bo-small" (click)="setAllIssuable()">
                        {{ i18n.t('rent.issue.allRemaining') }}
                      </button>
                    }
                  </th>
                  <th>{{ i18n.t('rent.dueOn') }}</th>
                  <th class="num">{{ i18n.t('rent.issue.invoices') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (line of issuable(); track line.id) {
                  <tr>
                    <td [attr.data-label]="i18n.t('rent.item')">
                      <span class="mono bo-soft">{{ line.sku }}</span>
                      <span>{{ name(line) }}</span>
                      @if (line.parentLineId) {
                        <span class="bo-state">{{ i18n.t('rent.accessory') }}</span>
                      }
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('rent.contracted')">{{ line.qty }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('rent.issued')">{{ line.issuedQty }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('rent.leftToIssue')">{{ line.leftToIssueQty }}</td>
                    <td class="num" [attr.data-label]="i18n.t('rent.issue.sendNow')">
                      <input
                        type="number"
                        min="0"
                        [max]="line.leftToIssueQty"
                        class="num mono bo-input-narrow"
                        [attr.aria-label]="i18n.t('rent.issue.sendNow') + ' — ' + name(line)"
                        [attr.aria-invalid]="problemOf(line) ? 'true' : null"
                        [ngModel]="qtyOf(line.id)"
                        (ngModelChange)="setQty(line.id, +$event)"
                      />
                      <button type="button" class="btn-ghost bo-small" (click)="setQty(line.id, line.leftToIssueQty)">
                        {{ i18n.t('rent.issue.all') }}
                      </button>
                      @if (problemOf(line); as problem) {
                        <span class="bo-error" role="alert">{{ i18n.t('rent.problem.' + problem) }}</span>
                      }
                    </td>
                    <td class="mono" [attr.data-label]="i18n.t('rent.dueOn')">
                      {{ qtyOf(line.id) > 0 ? dueOn(line) : '—' }}
                    </td>
                    <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('rent.issue.invoices')">
                      {{ i18n.money(lineInvoice(line)) }}
                    </td>
                  </tr>
                } @empty {
                  <tr>
                    <td colspan="7" class="bo-empty">
                      <strong>{{ i18n.t('rent.issue.nothingLeft') }}</strong>
                      <span>{{ i18n.t('rent.issue.nothingLeftHint') }}</span>
                    </td>
                  </tr>
                }
              </tbody>
              <tfoot>
                <tr>
                  <td [attr.data-label]="i18n.t('rent.issue.invoices')">{{ i18n.t('rent.issue.thisNoteInvoices') }}</td>
                  <td></td>
                  <td></td>
                  <td></td>
                  <td class="num mono" [attr.data-label]="i18n.t('register.qty')">{{ totalQty() }}</td>
                  <td></td>
                  <td class="num mono" [attr.data-label]="i18n.t('common.total')">{{ i18n.money(totalInvoice()) }}</td>
                </tr>
              </tfoot>
            </table>
          </div>

          <div class="bo-panel__body">
            <p class="bo-hint">{{ i18n.t('rent.issue.fullPeriodRule') }}</p>
          </div>

          @if (signatory() === 'missing') {
            <!-- The server would refuse this note; say it here, in the reader's words, and offer
                 the way back rather than a checkbox that leads to a refusal. -->
            <div class="bo-panel__body">
              <p class="notice danger" role="alert">
                <strong>{{ i18n.t('rent.sign.refusedTitle') }}</strong>
                {{ i18n.t('rent.sign.refusedBody') }}
              </p>
              <button type="button" class="btn-ghost" (click)="cancelled.emit()">
                {{ i18n.t('rent.sign.fixContract') }}
              </button>
            </div>
          } @else if (identityGaps().length > 0) {
            <div class="bo-panel__body">
              <p class="notice danger" role="alert">
                <strong>{{ i18n.t('rent.id.refusedTitle') }}</strong>
                {{ i18n.t('rent.id.refusedBody', { fields: identityGapWords() }) }}
              </p>
              <button type="button" class="btn-ghost" (click)="cancelled.emit()">
                {{ i18n.t('rent.sign.fixContract') }}
              </button>
            </div>
          } @else if (!d.contract.isLocked) {
            <div class="bo-panel__body">
              <p class="notice warn" role="alert">
                <strong>{{ i18n.t('rent.issue.locksTitle') }}</strong>
                {{ i18n.t('rent.issue.locksBody') }}
              </p>
              <div class="bo-check">
                <input id="ri-ack" type="checkbox" [ngModel]="acknowledged()" (ngModelChange)="acknowledged.set($event)" />
                <label for="ri-ack">{{ i18n.t('rent.issue.locksAck') }}</label>
              </div>
            </div>
          }

          <div class="ui-actionbar ui-actionbar--sticky">
            <button type="button" class="btn-ghost" (click)="cancelled.emit()">{{ i18n.t('common.cancel') }}</button>
            <button type="button" class="btn-primary" [disabled]="busy() || !canSave()" (click)="save()">
              {{ i18n.t('rent.issue.raise') }}
            </button>
          </div>
        </section>
      } @else {
        <p class="bo-loading">
          <span class="bo-loading__word">{{ i18n.t('common.loading') }}</span>
          <span class="bo-loading__rows" aria-hidden="true"></span>
        </p>
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      /* The action bar sticks to the foot of the screen while the form scrolls; a sticky child
         needs its panel not to clip it. */
      .form-panel {
        overflow: visible;
      }
    `,
  ],
})
export class RentalIssueNoteComponent implements OnInit {
  private readonly api = inject(RentalsApi);
  private readonly confirm = inject(ConfirmService);
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);
  private readonly regional = inject(RegionalService);

  readonly contractId = input.required<string>();
  readonly branches = input<AdminBranch[]>([]);
  readonly products = input<AdminProduct[]>([]);
  /** The company's rental settings, for the day cutoff hour. Absent reads as no cutoff. */
  readonly settings = input<RentalSettings | null>(null);

  /**
   * The note that was raised, not just the fact that one was: the shell shows a `done` sentence
   * after it and the Print link in that sentence needs the note's id (docs/07 §4.4). A listener
   * that only cares that the screen finished can go on ignoring the payload.
   */
  readonly done = output<IssueNote>();
  readonly cancelled = output<void>();

  readonly docs = signal<ContractDocuments | null>(null);

  private readonly chrome = inject(PageChrome, { optional: true });
  /** Names this screen in the page header: its title, and the contract it belongs to as a step back. */
  private readonly named = effect(() => {
    const contract = this.docs()?.contract;
    this.chrome?.heading.set(this.i18n.t('rent.newIssue'));
    this.chrome?.description.set(contract ? contract.customerName || '—' : null);
    this.chrome?.trail.set(contract ? [{ label: contract.number, link: '/backoffice/rentals/contracts/' + contract.id }] : []);
  });

  readonly branchId = signal<string | null>(null);
  readonly issuedOn = signal<string>(today(this.regional.settings().timeZone));
  /** The hour the goods leave, in the company's zone — now, unless the clerk says otherwise. */
  readonly issuedTime = signal<string>(nowTime(this.regional.settings().timeZone));
  readonly note = signal('');
  readonly driverName = signal('');
  readonly driverPhone = signal('');
  readonly truckNumber = signal('');
  /** The customer carries the goods himself: no driver or truck is asked (owner, 2026-10-02). */
  readonly customerTransport = signal(false);
  readonly touched = new TouchedFields();
  readonly acknowledged = signal(false);
  readonly quantities = signal<Record<string, number>>({});
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  /** Lines with something still to send. A line already fully out has no row to fill in. */
  readonly issuable = computed(() => (this.docs()?.contract.lines ?? []).filter((l) => l.leftToIssueQty > 0));

  readonly totalQty = computed(() =>
    this.issuable().reduce((sum, line) => sum + this.qtyOf(line.id), 0),
  );

  readonly totalInvoice = computed(() =>
    this.issuable().reduce((sum, line) => sum + this.lineInvoice(line), 0),
  );

  /** Who signed, from the contract row: the rule's mirror, asked before the server is. */
  readonly signatory = computed(() => {
    const contract = this.docs()?.contract;
    return contract ? signatoryState(contract) : 'predates';
  });

  readonly signedByKey = signedByKey;

  /** What the first note would be refused for beside the name — the server's rule asked first. */
  readonly identityGaps = computed(() => {
    const contract = this.docs()?.contract;
    return contract && this.signatory() === 'signed' ? identityMissing(contract) : [];
  });

  readonly identityGapWords = computed(() =>
    this.identityGaps().map((field) => this.i18n.t('rent.id.field.' + field)).join(this.i18n.t('rent.list.separator')),
  );

  readonly transportGaps = computed(() =>
    this.customerTransport() ? [] : transportMissing(this.driverName(), this.driverPhone(), this.truckNumber()),
  );

  /** The first day of this note's period: tomorrow for goods out at or after the cutoff hour. */
  readonly startDay = computed(() =>
    periodStartsOn(
      localTime(this.issuedAtUtc(), this.regional.settings().timeZone),
      this.settings()?.dayCutoffHour ?? null,
    ),
  );

  readonly startsNextDay = computed(() => this.startDay() !== this.issuedOn());

  private readonly issuedAtUtc = computed(() =>
    zonedTimeToUtcIso(this.issuedOn() || today(this.regional.settings().timeZone), this.issuedTime(), this.regional.settings().timeZone),
  );

  readonly canSave = computed(() => {
    const docs = this.docs();
    if (!docs || !this.branchId() || this.totalQty() <= 0) return false;
    if (this.issuable().some((line) => this.problemOf(line))) return false;
    // A blank signatory on a first note is refused by the server; the lock is not even offered.
    if (this.signatory() === 'missing' || this.identityGaps().length > 0) return false;
    return docs.contract.isLocked || this.acknowledged();
  });

  async ngOnInit(): Promise<void> {
    this.branchId.set(this.lock.defaultBranch(this.branches()));
    try {
      const docs = await this.api.readContract(this.contractId());
      this.docs.set(docs);
      // The issue usually leaves from the branch that signed the contract, so start there.
      // A non-admin issues from their own branch — the goods leave where the employee is.
      if (!this.lock.locked() && this.branches().some((b) => b.id === docs.contract.branchId)) this.branchId.set(docs.contract.branchId);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    }
  }

  qtyOf(lineId: string): number {
    return this.quantities()[lineId] ?? 0;
  }

  setQty(lineId: string, qty: number): void {
    this.quantities.update((q) => ({ ...q, [lineId]: Number.isFinite(qty) ? qty : 0 }));
  }

  setAllIssuable(): void {
    const next: Record<string, number> = {};
    for (const line of this.issuable()) {
      next[line.id] = line.leftToIssueQty;
    }
    this.quantities.set(next);
  }

  /** The engine's own refusal, asked before the server is: issues never exceed the contracted. */
  problemOf(line: RentalContractLine): string | null {
    const qty = this.qtyOf(line.id);
    if (qty === 0) return null;
    return issueProblem(engineLine(line), line.issuedQty, qty);
  }

  /** The line's own duration plus every extension already signed over it. */
  private entitled(line: RentalContractLine): number {
    const docs = this.docs();
    if (!docs) return line.durationDays;
    const lines = docs.contract.lines.map(engineLine);
    const self = engineLine(line);
    const extensions = engineExtensions(docs.extensions, this.regional.settings().timeZone);
    return entitledDays(
      entitlement(line.rentUnitPrice, durationOf(self, lines), extensionsFor(self, extensions)),
    );
  }

  /**
   * This batch's own clock: the period starts the day it goes out — or the next day after the
   * cutoff hour — never on the day the contract was signed.
   */
  dueOn(line: RentalContractLine): string {
    return formatRentalDay(periodEndsOn(this.startDay(), this.entitled(line)), this.i18n.lang());
  }

  rentalDay(day: string): string {
    return formatRentalDay(day, this.i18n.lang());
  }

  cutoffLabel(): string {
    return `${String(this.settings()?.dayCutoffHour ?? 0).padStart(2, '0')}:00`;
  }

  /** A left-to-right number inside a sentence that may be Arabic: isolated so its digits keep their order. */
  isolate(text: string): string {
    return `\u2066${text}\u2069`;
  }

  transportInvalid(field: 'driverName' | 'driverPhone' | 'truckNumber'): boolean {
    return this.touched.missing(field, this.transportGaps().includes(field) ? '' : 'ok');
  }

  /**
   * The full agreed period on what is about to go out. It is invoiced at issue, not at return —
   * everything else in the module follows from that, including the refund on an early return.
   */
  lineInvoice(line: RentalContractLine): number {
    const docs = this.docs();
    if (!docs) return 0;
    const lines = docs.contract.lines.map(engineLine);
    return rentalAmount(
      line.rentUnitPrice,
      durationOf(engineLine(line), lines),
      this.qtyOf(line.id),
      this.regional.decimals(),
    );
  }

  name(line: RentalContractLine): string {
    return lineName(line, this.products(), this.i18n.lang());
  }

  branchName(branch: AdminBranch): string {
    return localizedName(branch, this.i18n.lang());
  }

  async save(): Promise<void> {
    // Said on the fields rather than by a greyed button: the server refuses the note without them.
    this.touched.touchAll('driverName', 'driverPhone', 'truckNumber');
    if (this.transportGaps().length > 0 || !this.canSave()) return;
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('rent.issue.raise'))))) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const note = await this.api.issue(this.contractId(), {
        branchId: this.branchId()!,
        lines: this.issuable()
          .filter((line) => this.qtyOf(line.id) > 0)
          .map((line) => ({ contractLineId: line.id, qty: this.qtyOf(line.id) })),
        // The day and the hour in the company's zone: the hour decides the first day of the
        // period once a cutoff is set, so it travels as typed rather than as midday.
        issuedAtUtc: this.issuedOn() ? this.issuedAtUtc() : null,
        note: this.note().trim() || null,
        driverName: this.driverName().trim(),
        driverPhone: this.driverPhone().trim(),
        truckNumber: this.truckNumber().trim(),
        customerTransport: this.customerTransport(),
      });
      this.done.emit(note);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
