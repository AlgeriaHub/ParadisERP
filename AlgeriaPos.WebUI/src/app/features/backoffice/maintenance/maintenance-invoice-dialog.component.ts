import { Component, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { DateInputComponent } from '../../../ui/date-input.component';
import { DialogComponent } from '../../../ui/dialog.component';
import { DEFAULT_DUE_DAYS, addDays, midday, today } from '../receivables/receivable-view';
import { MaintenanceCustomerApi, MaintenanceJobInvoice } from './maintenance-customer-api.service';
import { maintenanceError } from './maintenance-words';

/** The job the dialog invoices: its id and number, and its price and VAT choice when known. */
export interface InvoiceableJob {
  id: string;
  number: string;
  price?: number | null;
  taxMode?: 'Taxed' | 'Untaxed';
}

/**
 * Raise a customer job's one invoice (Phase M5a, docs/maintenance/plan.md §3.2), from the job's Price card or the Jobs
 * list's ⋯ menu. The due date (on the company's calendar, `app-date-input`) and the VAT tick: every bill starts without
 * VAT and the seller ticks it when the customer asks (owner, 2026-11-05); then the country profile's default rate is added
 * on top of the price, which is before tax. The server posts it (411 / income / 4457) and refuses a second one.
 */
@Component({
  selector: 'app-maintenance-invoice-dialog',
  standalone: true,
  imports: [FormsModule, DateInputComponent, DialogComponent],
  template: `
    <app-dialog [open]="!!job()" size="sm" [heading]="i18n.t('maint.invoice.heading', { n: job()?.number ?? '' })" (dismiss)="dismiss.emit()">
      @if (job(); as j) {
        <div class="bo-form">
          @if (error()) {
            <p class="notice danger" role="alert">{{ error() }}</p>
          }
          <p class="bo-hint">{{ i18n.t('maint.invoice.lead') }}</p>
          @if (j.price !== null && j.price !== undefined) {
            <p>{{ i18n.t('maint.invoice.price', { amount: i18n.money(j.price) }) }}</p>
          } @else {
            <p class="bo-hint">{{ i18n.t('maint.invoice.proposal') }}</p>
          }
          <div class="bo-field">
            <label for="mi-due">{{ i18n.t('maint.invoice.due') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
            <app-date-input id="mi-due" [ngModel]="dueOn()" (ngModelChange)="dueOn.set($event || '')" />
          </div>
          <label class="bo-check">
            <input id="mi-vat" type="checkbox" [ngModel]="taxed()" (ngModelChange)="taxed.set($event)" />
            {{ i18n.t('maint.invoice.vat', { rate: rate() }) }}
          </label>
          <p class="bo-hint">{{ i18n.t(taxed() ? 'maint.invoice.vatOn' : 'maint.invoice.vatOff') }}</p>
        </div>
      }
      <div dialogFoot class="ui-actionbar">
        <button type="button" class="btn-ghost" (click)="dismiss.emit()">{{ i18n.t('common.close') }}</button>
        <button type="button" class="btn-primary" [disabled]="busy() || !dueOn()" (click)="raise()">{{ i18n.t('maint.invoice.raise') }}</button>
      </div>
    </app-dialog>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
})
export class MaintenanceInvoiceDialogComponent {
  private readonly api = inject(MaintenanceCustomerApi);
  private readonly regional = inject(RegionalService);
  readonly i18n = inject(I18nService);

  /** The job to invoice; null keeps the dialog closed. */
  readonly job = input<InvoiceableJob | null>(null);
  /** The rate added when VAT is ticked (the Price card knows it; the profile's default otherwise). */
  readonly taxRate = input<number | null>(null);
  readonly raised = output<MaintenanceJobInvoice>();
  readonly dismiss = output<void>();

  readonly dueOn = signal('');
  readonly taxed = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  constructor() {
    // Each opening starts afresh: due in the usual days from the company's today, VAT as the job says (untaxed unless ticked).
    effect(() => {
      const job = this.job();
      untracked(() => {
        if (!job) return;
        this.dueOn.set(addDays(today(this.regional.settings().timeZone), DEFAULT_DUE_DAYS));
        this.taxed.set(job.taxMode === 'Taxed');
        this.error.set(null);
      });
    });
  }

  rate(): string {
    const rate = this.taxRate() ?? this.regional.settings().defaultTaxRate ?? 0;
    return `${rate} %`;
  }

  async raise(): Promise<void> {
    const job = this.job();
    if (!job || !this.dueOn()) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      this.raised.emit(await this.api.invoice(job.id, midday(this.dueOn()), this.taxed() ? 'Taxed' : 'Untaxed'));
    } catch (e) {
      this.error.set(maintenanceError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }
}
