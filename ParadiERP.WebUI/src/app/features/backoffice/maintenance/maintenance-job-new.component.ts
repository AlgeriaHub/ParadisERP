import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BranchLock } from '../../../core/branch-lock';
import { I18nService } from '../../../core/i18n.service';
import { localizedName } from '../../../core/multilingual';
import { TableStateComponent } from '../../../ui/table-state.component';
import { formatQty } from '../backoffice-format';
import { PageChrome } from '../shell/page-chrome';
import {
  JOB_KINDS,
  MaintenanceApi,
  MaintenanceBucket,
  MaintenanceJobKind,
  MaintenanceReworkCandidate,
  MaintenanceWaiting,
  MaintenanceWaitingLine,
  jobRequestId,
} from './maintenance-api.service';
import { MaintenanceCustomerApi, MaintenanceNewJobOptions } from './maintenance-customer-api.service';
import { maintenanceError } from './maintenance-words';

/**
 * Maintenance › New job (Phase M1, docs/maintenance/plan.md §2.2 and §7): the kind first, then what it repairs. A
 * recharged job takes a return-note line's repairable units that no job covers yet; an internal one takes units in
 * maintenance at a branch that no job holds. Both choices come from Waiting for repair, so nothing is offered that the
 * server would refuse; it checks again under its lock. Opening moves no stock. Prefilled from the address:
 * `?line=<return-note line>` or `?product=<id>&branch=<id>`.
 */
@Component({
  selector: 'app-maintenance-job-new',
  standalone: true,
  imports: [FormsModule, TableStateComponent],
  template: `
    <section class="bo-panel">
      <div class="bo-panel__head">
        <h2 class="bo-panel__title">{{ i18n.t('maint.jobs.new') }}</h2>
        <p class="bo-panel__note">{{ i18n.t('maint.new.lead') }}</p>
      </div>
      @if (loading()) {
        <app-table-state kind="loading" />
      } @else {
        <div class="bo-panel__body bo-form">
          @if (error()) {
            <p class="notice danger" role="alert">{{ error() }}</p>
          }
          <div class="bo-field">
            <label for="mn-kind">{{ i18n.t('maint.job.kind') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
            <select id="mn-kind" [ngModel]="kind()" (ngModelChange)="setKind($event)">
              @for (k of kinds(); track k) {
                <option [ngValue]="k">{{ i18n.t('maint.kind.' + k) }}</option>
              }
            </select>
            <p class="bo-hint">{{ i18n.t('maint.kind.' + kind() + '.help') }}</p>
          </div>

          @if (kind() === 'Customer') {
            <!-- Phase M5a (MS-06): an outside customer's equipment, described in words; it takes nothing from the company's stock. -->
            <div class="bo-field">
              <label for="mn-customer">{{ i18n.t('maint.customer.customer') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <select id="mn-customer" [ngModel]="customerId()" (ngModelChange)="setCustomer($event)">
                <option [ngValue]="null">{{ i18n.t('maint.customer.pickCustomer') }}</option>
                @for (c of options().customers; track c.id) {
                  <option [ngValue]="c.id">{{ c.fullName }} ({{ c.code }})</option>
                }
              </select>
            </div>
            <!-- Phase M7c (MS-27, MS-37): a free rework of the customer's completed job, while its guarantee runs. -->
            @if (customerId() && reworkCandidates().length) {
              <div class="bo-field">
                <label for="mn-rework">{{ i18n.t('maint.rework.of') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
                <select id="mn-rework" [ngModel]="reworkOfId()" (ngModelChange)="setRework($event)" aria-describedby="mn-rework-help">
                  <option [ngValue]="null">{{ i18n.t('maint.rework.none') }}</option>
                  @for (r of reworkCandidates(); track r.id) {
                    <option [ngValue]="r.id">{{ reworkLabel(r) }}</option>
                  }
                </select>
                <p id="mn-rework-help" class="bo-hint">{{ i18n.t(reworkOfId() ? 'maint.rework.freeHint' : 'maint.rework.pickHint') }}</p>
              </div>
            }
            <div class="bo-field">
              <label for="mn-equipment">{{ i18n.t('maint.customer.equipment') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="mn-equipment" maxlength="200" [(ngModel)]="equipment" />
              <p class="bo-hint">{{ i18n.t('maint.customer.equipmentHint') }}</p>
            </div>
            <div class="bo-field">
              <label for="mn-cproduct">{{ i18n.t('maint.job.product') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
              <select id="mn-cproduct" [ngModel]="customerProductId()" (ngModelChange)="customerProductId.set($event)">
                <option [ngValue]="null">{{ i18n.t('maint.customer.noProduct') }}</option>
                @for (p of options().products; track p.id) {
                  <option [ngValue]="p.id">{{ name(p) }} ({{ p.sku }})</option>
                }
              </select>
            </div>
          } @else if (kind() === 'Recharged') {
            <div class="bo-field">
              <label for="mn-line">{{ i18n.t('maint.new.line') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <select id="mn-line" [ngModel]="lineId()" (ngModelChange)="setLine($event)">
                @for (l of lines(); track l.returnNoteLineId) {
                  <option [ngValue]="l.returnNoteLineId">{{ lineLabel(l) }}</option>
                }
              </select>
              @if (!lines().length) {
                <p class="bo-hint">{{ i18n.t('maint.new.noLines') }}</p>
              }
              @if (chosenLine(); as l) {
                <p class="bo-hint">{{ i18n.t('maint.new.lineFacts', { damaged: qty(l.damagedQty), covered: qty(l.coveredQty), recharge: i18n.money(l.recharge) }) }}</p>
              }
            </div>
          } @else {
            <div class="bo-field">
              <label for="mn-bucket">{{ i18n.t('maint.new.bucket') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <select id="mn-bucket" [ngModel]="bucketKey()" (ngModelChange)="setBucket($event)">
                @for (b of buckets(); track key(b)) {
                  <option [ngValue]="key(b)">{{ bucketLabel(b) }}</option>
                }
              </select>
              @if (!buckets().length) {
                <p class="bo-hint">{{ i18n.t('maint.new.noBuckets') }}</p>
              }
            </div>
          }

          <div class="bo-field">
            <label for="mn-qty">{{ i18n.t('maint.job.qty') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
            <input id="mn-qty" type="number" min="0" step="any" class="num mono" [(ngModel)]="qtyValue" />
            @if (kind() !== 'Customer') {
              <p class="bo-hint">{{ i18n.t('maint.new.qtyMax', { n: qty(maxQty()) }) }}</p>
            } @else {
              <p class="bo-hint">{{ i18n.t('maint.customer.qtyHint') }}</p>
            }
          </div>
          <div class="bo-field">
            <label for="mn-ref">{{ i18n.t('maint.job.itemReference') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
            <input id="mn-ref" maxlength="120" [(ngModel)]="itemReference" />
          </div>
          <div class="bo-field">
            <label for="mn-desc">{{ i18n.t('maint.job.description') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
            <textarea id="mn-desc" rows="3" maxlength="1000" [(ngModel)]="description"></textarea>
          </div>
        </div>
        <div class="bo-panel__foot ui-actionbar">
          <button type="button" class="btn-ghost" (click)="cancelled.emit()">{{ i18n.t('common.cancel') }}</button>
          <button type="button" class="btn-primary" [disabled]="busy() || !ready()" (click)="submit()">{{ i18n.t('maint.new.open') }}</button>
        </div>
      }
    </section>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
})
export class MaintenanceJobNewComponent implements OnInit {
  private readonly api = inject(MaintenanceApi);
  private readonly customers = inject(MaintenanceCustomerApi);
  private readonly branchLock = inject(BranchLock);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  /** From the address: a return-note line to repair, or a product in maintenance at a branch. */
  readonly line = input<string | null>(null);
  readonly product = input<string | null>(null);
  readonly branch = input<string | null>(null);
  readonly opened = output<string>();
  readonly cancelled = output<void>();

  /** Phase M5a: what a customer job may name, and whether MS-06 is on (the Customer kind is offered only then). */
  readonly options = signal<MaintenanceNewJobOptions>({ customerJobs: false, customers: [], products: [] });
  readonly kinds = computed(() => JOB_KINDS.filter((k) => k !== 'Customer' || this.options().customerJobs));
  readonly customerId = signal<string | null>(null);
  readonly customerProductId = signal<string | null>(null);
  /** Phase M7c: the customer's completed jobs whose guarantee still runs, and the one this job redoes free, if any. */
  readonly reworkCandidates = signal<MaintenanceReworkCandidate[]>([]);
  readonly reworkOfId = signal<string | null>(null);
  equipment = '';
  readonly kind = signal<MaintenanceJobKind>('Internal');
  readonly waiting = signal<MaintenanceWaiting>({ buckets: [], lines: [] });
  readonly lineId = signal<string | null>(null);
  readonly bucketKey = signal<string | null>(null);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  qtyValue: number | null = null;
  itemReference = '';
  description = '';
  private readonly requestId = jobRequestId();

  readonly lines = computed(() => this.waiting().lines.filter((l) => l.openQty > 0));
  readonly buckets = computed(() => this.waiting().buckets.filter((b) => b.free > 0));
  readonly chosenLine = computed(() => this.lines().find((l) => l.returnNoteLineId === this.lineId()) ?? null);
  readonly chosenBucket = computed(() => this.buckets().find((b) => this.key(b) === this.bucketKey()) ?? null);
  readonly maxQty = computed(() => (this.kind() === 'Recharged' ? (this.chosenLine()?.openQty ?? 0) : (this.chosenBucket()?.free ?? 0)));

  async ngOnInit(): Promise<void> {
    this.chrome?.heading.set(this.i18n.t('maint.jobs.new'));
    this.chrome?.description.set(null);
    this.loading.set(true);
    try {
      const [waiting, options] = await Promise.all([this.api.waiting(), this.customers.newJobOptions().catch(() => null)]);
      this.waiting.set(waiting);
      if (options) this.options.set(options);
    } catch (e) {
      this.error.set(maintenanceError(e, (k) => this.i18n.t(k)));
    } finally {
      this.loading.set(false);
    }
    const wanted = this.line();
    if (wanted && this.lines().some((l) => l.returnNoteLineId === wanted)) {
      this.kind.set('Recharged');
      this.setLine(wanted);
    } else {
      const prefill = this.buckets().find((b) => b.productId === this.product() && (!this.branch() || b.branchId === this.branch()));
      this.setBucket(prefill ? this.key(prefill) : this.buckets()[0] ? this.key(this.buckets()[0]) : null);
    }
  }

  key(b: MaintenanceBucket): string {
    return `${b.branchId}|${b.productId}`;
  }

  qty(value: number): string {
    return formatQty(value, this.i18n.lang());
  }

  name(x: { nameAr: string; nameFr: string; nameEn: string; sku: string }): string {
    return localizedName(x, this.i18n.lang()) || x.sku;
  }

  lineLabel(l: MaintenanceWaitingLine): string {
    return `${l.returnNoteNumber} · ${l.contractNumber} · ${this.name(l)} (${l.sku}) — ${this.i18n.t('maint.new.openUnits', { n: this.qty(l.openQty) })}`;
  }

  bucketLabel(b: MaintenanceBucket): string {
    return `${b.branchName} — ${this.name(b)} (${b.sku}) — ${this.i18n.t('maint.new.freeUnits', { n: this.qty(b.free) })}`;
  }

  setKind(kind: MaintenanceJobKind): void {
    this.kind.set(kind);
    if (kind === 'Customer') this.qtyValue = 1;
    else if (kind === 'Recharged') this.setLine(this.lineId() ?? this.lines()[0]?.returnNoteLineId ?? null);
    else this.setBucket(this.bucketKey() ?? (this.buckets()[0] ? this.key(this.buckets()[0]) : null));
  }

  /** Phase M7c: a new customer named, the jobs a free rework may be opened against are read for them. */
  async setCustomer(id: string | null): Promise<void> {
    this.customerId.set(id);
    this.reworkOfId.set(null);
    this.reworkCandidates.set([]);
    if (!id) return;
    try {
      const candidates = await this.api.reworkCandidates(id, this.branchLock.ownBranchId());
      if (this.customerId() === id) this.reworkCandidates.set(candidates);
    } catch {
      // No candidates read: an ordinary customer job is still offered.
    }
  }

  /** A rework takes the equipment of the job it redoes unless the user already said it. */
  setRework(id: string | null): void {
    this.reworkOfId.set(id);
    const original = this.reworkCandidates().find((r) => r.id === id);
    if (original?.equipment && !this.equipment.trim()) this.equipment = original.equipment;
  }

  reworkLabel(r: MaintenanceReworkCandidate): string {
    return this.i18n.t('maint.rework.candidate', { number: r.number, equipment: r.equipment ?? '—', until: this.i18n.date(r.guaranteeUntil) });
  }

  setLine(id: string | null): void {
    this.lineId.set(id);
    this.qtyValue = this.chosenLine()?.openQty ?? null;
  }

  setBucket(key: string | null): void {
    this.bucketKey.set(key);
    this.qtyValue = this.chosenBucket()?.free ?? null;
  }

  ready(): boolean {
    const n = Number(this.qtyValue);
    if (this.kind() === 'Customer') return !!this.customerId() && !!this.equipment.trim() && Number.isFinite(n) && n > 0;
    const chosen = this.kind() === 'Recharged' ? !!this.chosenLine() : !!this.chosenBucket();
    return chosen && Number.isFinite(n) && n > 0 && n <= this.maxQty();
  }

  async submit(): Promise<void> {
    if (!this.ready()) return;
    this.busy.set(true);
    this.error.set(null);
    const recharged = this.kind() === 'Recharged';
    const customer = this.kind() === 'Customer';
    const line = this.chosenLine();
    const bucket = this.chosenBucket();
    try {
      const job = await this.api.create({
        clientRequestId: this.requestId,
        // A customer job is opened at the user's own branch (the server makes it theirs for everyone but the Admin).
        branchId: customer ? this.branchLock.ownBranchId() : recharged ? (line?.branchId ?? null) : (bucket?.branchId ?? null),
        kind: this.kind(),
        productId: customer ? this.customerProductId() : recharged ? (line?.productId ?? null) : (bucket?.productId ?? null),
        qty: Number(this.qtyValue),
        returnNoteLineId: recharged ? (line?.returnNoteLineId ?? null) : null,
        itemReference: this.itemReference.trim() || null,
        description: this.description.trim() || null,
        customerId: customer ? this.customerId() : null,
        equipment: customer ? this.equipment.trim() || null : null,
        reworkOfJobId: customer ? this.reworkOfId() : null,
      });
      this.opened.emit(job.id);
    } catch (e) {
      this.error.set(maintenanceError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }
}
