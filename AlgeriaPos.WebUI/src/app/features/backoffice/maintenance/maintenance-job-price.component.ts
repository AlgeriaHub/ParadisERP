import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { problemCode } from '../../../core/admin.service';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { mayDo } from '../../../core/landing';
import { RegionalService } from '../../../core/regional.service';
import { TableStateComponent } from '../../../ui/table-state.component';
import { formatQty } from '../backoffice-format';
import { PrintService } from '../../print/print.service';
import { MaintenanceJob } from './maintenance-api.service';
import { MaintenanceCustomerApi, MaintenanceJobInvoice, MaintenanceJobPrice } from './maintenance-customer-api.service';
import { MaintenanceInvoiceDialogComponent } from './maintenance-invoice-dialog.component';
import { maintenanceError } from './maintenance-words';

/**
 * A customer job's Price card (Phase M5a, docs/maintenance/plan.md §3.2, MS-20 to MS-25): the basis in force and what it
 * proposes — Quoted: nothing, the price is typed; Itemised: parts at their sale price before tax + hours × the labour
 * charge rate + outside work at cost + the markup on it; Cost-plus: the job's cost + the markup —, the price before tax a
 * `maintenance.pricing` holder sets, whether the invoice will carry VAT (untaxed unless ticked), and, below the cost, the
 * loss in words (MS-25: shown, or confirmed before it is kept; never refused). The cost, the loss and the cost-plus
 * proposal are the server's to withhold (labour privacy, §6.2); a withheld figure is said in words. Once the job is
 * completed, "Raise the invoice" (finance.invoices.create) bills it once.
 */
@Component({
  selector: 'app-maintenance-job-price',
  standalone: true,
  imports: [FormsModule, MaintenanceInvoiceDialogComponent, TableStateComponent],
  template: `
    <section class="bo-panel bo-panel--operational" aria-labelledby="mjp-title">
      <div class="bo-panel__head">
        <h2 class="bo-panel__title" id="mjp-title">{{ i18n.t('maint.price.title') }}</h2>
        @if (price(); as p) {
          <p class="bo-panel__note">{{ i18n.t('maint.price.basisLead', { basis: i18n.t('maint.customerPrice.' + p.basis) }) }}</p>
        }
      </div>
      <div class="bo-panel__body bo-form">
        @if (error()) {
          <p class="notice danger" role="alert">{{ error() }}</p>
        }
        @if (done()) {
          <p class="notice ok" role="status">{{ done() }}</p>
        }
        @if (loading() && !price()) {
          <app-table-state kind="loading" />
        } @else if (price(); as p) {
          <dl class="ui-facts">
            @if (p.basis === 'Itemised') {
              <div>
                <dt>{{ i18n.t('maint.price.parts') }}</dt>
                <dd class="mono">{{ i18n.money(p.parts ?? 0) }} <span class="ui-sub">{{ i18n.t('maint.price.partsNote', { n: qty(p.partsUsedQty) }) }}</span></dd>
              </div>
              <div>
                <dt>{{ i18n.t('maint.price.labour') }}</dt>
                <dd class="mono">{{ i18n.money(p.labour ?? 0) }} <span class="ui-sub">{{ i18n.t('maint.price.labourNote', { h: qty(p.hours), rate: i18n.money(p.labourChargePerHour) }) }}</span></dd>
              </div>
              <div>
                <dt>{{ i18n.t('maint.price.outsideWork') }}</dt>
                <dd class="mono">{{ i18n.money(p.outsideWork) }}</dd>
              </div>
              <div>
                <dt>{{ i18n.t('maint.price.markup', { pct: p.markupPercent }) }}</dt>
                <dd class="mono">{{ i18n.money(p.markup ?? 0) }}</dd>
              </div>
            }
            @if (p.basis === 'CostPlus') {
              <div>
                <dt>{{ i18n.t('maint.price.cost') }}</dt>
                <dd class="mono">{{ money(p.costBase) }}</dd>
              </div>
              <div>
                <dt>{{ i18n.t('maint.price.markup', { pct: p.markupPercent }) }}</dt>
                <dd class="mono">{{ money(p.markup) }}</dd>
              </div>
            }
            @if (p.basis !== 'Quoted') {
              <div>
                <dt>{{ i18n.t('maint.price.proposed') }}</dt>
                <dd class="mono"><strong>{{ money(p.proposed) }}</strong></dd>
              </div>
            }
            @if (p.basis !== 'CostPlus' && p.costShown) {
              <div>
                <dt>{{ i18n.t('maint.price.cost') }}</dt>
                <dd class="mono">{{ money(p.cost) }} @if (p.costProvisional) { <span class="ui-sub">{{ i18n.t('maint.cost.status.provisional') }}</span> }</dd>
              </div>
            }
            <div>
              <dt>{{ i18n.t('maint.price.price') }}</dt>
              <dd class="mono">
                @if (p.price !== null) {
                  <strong>{{ i18n.money(p.price) }}</strong>
                  <span class="ui-sub">{{ i18n.t('maint.price.pricedBy', { who: p.pricedBy ?? '—' }) }}</span>
                } @else {
                  {{ i18n.t('maint.price.notPriced') }}
                }
              </dd>
            </div>
            <div>
              <dt>{{ i18n.t('maint.price.vat') }}</dt>
              <dd>{{ i18n.t(p.taxMode === 'Taxed' ? 'maint.price.vatTicked' : 'maint.price.vatNone', { rate: p.taxRateWhenTaxed }) }}</dd>
            </div>
            @if (p.invoiceNumber) {
              <div>
                <dt>{{ i18n.t('maint.price.invoice') }}</dt>
                <dd><span class="mono" dir="ltr">{{ p.invoiceNumber }}</span></dd>
              </div>
            }
          </dl>

          @if (p.basis === 'CostPlus' && !p.costShown) {
            <p class="notice info" role="status">{{ i18n.t(p.costAvailable ? 'maint.price.costHidden' : 'maint.price.noCost') }}</p>
          }
          @if (p.belowCost) {
            <p class="notice warn" role="status">
              {{ p.loss !== null ? i18n.t('maint.price.loss', { amount: i18n.money(p.loss) }) : i18n.t('maint.price.lossNoAmount') }}
              @if (p.belowCostConfirmedBy) {
                {{ i18n.t('maint.price.lossConfirmed', { who: p.belowCostConfirmedBy }) }}
              }
            </p>
          }

          @if (editable()) {
            <div class="bo-field">
              <label for="mjp-price">{{ i18n.t('maint.price.priceField') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="mjp-price" type="number" min="0" class="num mono" [step]="regional.moneyStep()" [(ngModel)]="draft" />
              @if (p.proposed !== null) {
                <p class="bo-hint">
                  <button type="button" class="btn-ghost" (click)="draft = p.proposed">{{ i18n.t('maint.price.useProposal', { amount: i18n.money(p.proposed) }) }}</button>
                </p>
              }
            </div>
            <label class="bo-check">
              <input id="mjp-vat" type="checkbox" [(ngModel)]="taxed" />
              {{ i18n.t('maint.invoice.vat', { rate: p.taxRateWhenTaxed + ' %' }) }}
            </label>
            @if (askConfirm()) {
              <p class="notice warn" role="alert">{{ i18n.t('maint.price.confirmLead') }}</p>
              <label class="bo-check">
                <input id="mjp-confirm" type="checkbox" [(ngModel)]="confirmBelowCost" />
                {{ i18n.t('maint.price.confirm') }}
              </label>
            }
            <div class="ui-actionbar">
              <button type="button" class="btn-primary" [disabled]="busy() || !validDraft() || (askConfirm() && !confirmBelowCost)" (click)="save()">
                {{ i18n.t('maint.price.save') }}
              </button>
            </div>
          }

          <div class="ui-actionbar">
            @if (p.invoiceId) {
              <button type="button" class="btn-ghost" (click)="print(p.invoiceId)">{{ i18n.t('maint.price.printInvoice') }}</button>
            } @else if (canInvoice() && job().status === 'Completed') {
              <button type="button" class="btn-primary" (click)="invoicing.set(true)">{{ i18n.t('maint.invoice.raise') }}</button>
            } @else if (job().status !== 'Cancelled') {
              <p class="bo-hint">{{ i18n.t('maint.price.invoiceAfterCompletion') }}</p>
            }
          </div>
        }
      </div>
    </section>

    <app-maintenance-invoice-dialog
      [job]="invoiceTarget()"
      [taxRate]="price()?.taxRateWhenTaxed ?? null"
      (raised)="onRaised($event)"
      (dismiss)="invoicing.set(false)"
    />
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
})
export class MaintenanceJobPriceComponent implements OnInit {
  private readonly api = inject(MaintenanceCustomerApi);
  private readonly auth = inject(AuthService);
  private readonly printer = inject(PrintService);
  readonly regional = inject(RegionalService);
  readonly i18n = inject(I18nService);

  readonly job = input.required<MaintenanceJob>();
  /** The invoice was raised: the job is Invoiced now. */
  readonly invoiced = output<MaintenanceJob>();

  readonly price = signal<MaintenanceJobPrice | null>(null);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly done = signal<string | null>(null);
  readonly askConfirm = signal(false);
  readonly invoicing = signal(false);
  draft: number | null = null;
  taxed = false;
  confirmBelowCost = false;

  /** What the dialog invoices; one object per opening, so the dialog's own choices are not reset under the clerk. */
  readonly invoiceTarget = computed(() => {
    if (!this.invoicing()) return null;
    const p = this.price();
    return { id: this.job().id, number: this.job().number, price: p?.price ?? null, taxMode: p?.taxMode };
  });
  readonly canPrice = computed(() => mayDo(this.auth.currentUser(), 'maintenance.pricing'));
  readonly canInvoice = computed(() => mayDo(this.auth.currentUser(), 'finance.invoices.create'));
  readonly editable = computed(() => {
    const status = this.job().status;
    return this.canPrice() && !this.price()?.invoiceId && status !== 'Invoiced' && status !== 'Cancelled';
  });

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.show(await this.api.price(this.job().id));
    } catch (e) {
      this.error.set(maintenanceError(e, (k) => this.i18n.t(k)));
    } finally {
      this.loading.set(false);
    }
  }

  validDraft(): boolean {
    const n = Number(this.draft);
    return this.draft !== null && Number.isFinite(n) && n > 0;
  }

  async save(): Promise<void> {
    if (!this.validDraft()) return;
    this.busy.set(true);
    this.error.set(null);
    this.done.set(null);
    try {
      this.show(await this.api.setPrice(this.job().id, Number(this.draft), this.taxed ? 'Taxed' : 'Untaxed', this.askConfirm() && this.confirmBelowCost));
      this.askConfirm.set(false);
      this.confirmBelowCost = false;
      this.done.set(this.i18n.t('maint.price.saved'));
    } catch (e) {
      // MS-25 "needs confirmation": the price is below the cost; it is kept once confirmed, never refused.
      if (problemCode(e) === 'price_below_cost') this.askConfirm.set(true);
      else this.error.set(maintenanceError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }

  onRaised(result: MaintenanceJobInvoice): void {
    this.invoicing.set(false);
    this.done.set(this.i18n.t('maint.invoice.raised', { n: result.invoiceNumber }) + (result.warning ? ` ${result.warning}` : ''));
    this.invoiced.emit(result.job);
    void this.load();
  }

  print(invoiceId: string): void {
    this.printer.open('invoice', invoiceId);
  }

  /** A figure the server left out (labour privacy, or no cost yet) is said in words, never a zero. */
  money(value: number | null): string {
    return value === null ? this.i18n.t('maint.cost.notShown') : this.i18n.money(value);
  }

  qty(value: number): string {
    return formatQty(value, this.i18n.lang());
  }

  private show(p: MaintenanceJobPrice): void {
    this.price.set(p);
    this.draft = p.price ?? p.proposed;
    this.taxed = p.taxMode === 'Taxed';
  }
}
