import { Component, OnInit, effect, inject, input, output, signal } from '@angular/core';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest } from '../../../ui/confirm-requests';
import { PageChrome } from '../shell/page-chrome';
import { FormsModule } from '@angular/forms';
import { adminErrorMessage } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ContractDocuments, RentalsApi } from './rentals-api.service';
import { today } from './rental-view';

/**
 * The owner's extension (2026-10-02, answer 8): a new contract for the customer, marked as the
 * extension of this one. Every unit still out is carried over at the price it was signed at, with
 * no issue note and nothing leaving the yard; the new period starts the day the first one ends,
 * whether it is asked for before or after that day. How the customer asked — by phone or in
 * person — is not recorded, because it does not matter.
 */
@Component({
  selector: 'app-rental-extend-new',
  standalone: true,
  imports: [FormsModule, DateInputComponent],
  template: `
    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (docs(); as d) {
        <!-- The title is the page header's; Cancel sits with Save in the action bar. -->
        <section class="bo-panel form-panel">
          <div class="bo-panel__head">
            <p class="bo-meta">
              <span class="mono">{{ d.contract.number }}</span>
              <span>{{ d.contract.customerName || '—' }}</span>
            </p>
            <p class="bo-panel__note">{{ i18n.t('rent.extendNew.note') }}</p>
          </div>
          <div class="bo-panel__body bo-form">
            <div class="bo-fields">
              <div class="bo-field" [class.is-invalid]="tried() && !(days() >= 1)">
                <label for="ren-days">{{ i18n.t('rent.extendNew.days') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                <input id="ren-days" type="number" min="1" step="1" class="num mono" [ngModel]="days()" (ngModelChange)="days.set(+$event)" />
                @if (tried() && !(days() >= 1)) {
                  <span class="bo-error" role="alert">{{ i18n.t('rent.extendNew.daysRequired') }}</span>
                }
              </div>
              <div class="bo-field">
                <label for="ren-signed">{{ i18n.t('rent.signedOn') }}</label>
                <app-date-input id="ren-signed" [ngModel]="signedOn()" (ngModelChange)="signedOn.set($event)" />
              </div>
              <div class="bo-field bo-field--wide">
                <label for="ren-notes">{{ i18n.t('rent.notes') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
                <input id="ren-notes" maxlength="500" [ngModel]="notes()" (ngModelChange)="notes.set($event)" />
              </div>
            </div>
          </div>
          <div class="ui-actionbar ui-actionbar--sticky">
            <button type="button" class="btn-ghost" (click)="cancelled.emit()">{{ i18n.t('common.cancel') }}</button>
            <button type="button" class="btn-primary" [disabled]="busy()" (click)="save()">{{ i18n.t('rent.extendNew.save') }}</button>
          </div>
        </section>
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
export class RentalExtendNewComponent implements OnInit {
  private readonly api = inject(RentalsApi);
  private readonly regional = inject(RegionalService);
  readonly i18n = inject(I18nService);

  readonly contractId = input.required<string>();
  /** The new contract's id. */
  readonly done = output<string>();
  readonly cancelled = output<void>();

  readonly docs = signal<ContractDocuments | null>(null);
  private readonly confirm = inject(ConfirmService);

  private readonly chrome = inject(PageChrome, { optional: true });
  /** Names this screen in the page header: its title, and the contract it belongs to as a step back. */
  private readonly named = effect(() => {
    const contract = this.docs()?.contract;
    this.chrome?.heading.set(this.i18n.t('rent.extendNew.title'));
    this.chrome?.description.set(contract ? contract.customerName || '—' : null);
    this.chrome?.trail.set(contract ? [{ label: contract.number, link: '/backoffice/rentals/contracts/' + contract.id }] : []);
  });

  readonly days = signal(30);
  readonly signedOn = signal(today(this.regional.settings().timeZone));
  readonly notes = signal('');
  readonly busy = signal(false);
  readonly tried = signal(false);
  readonly error = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    try {
      this.docs.set(await this.api.readContract(this.contractId()));
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    }
  }

  async save(): Promise<void> {
    this.tried.set(true);
    if (!(this.days() >= 1)) return;
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('rent.extendNew.save'))))) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const created = await this.api.extendAsNew(this.contractId(), {
        durationDays: Math.floor(this.days()),
        // Midday: a date field means a day, and midnight UTC is the day before west of Greenwich.
        signedAtUtc: this.signedOn() ? new Date(`${this.signedOn()}T12:00:00`).toISOString() : null,
        notes: this.notes().trim() || null,
      });
      this.done.emit(created.id);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
