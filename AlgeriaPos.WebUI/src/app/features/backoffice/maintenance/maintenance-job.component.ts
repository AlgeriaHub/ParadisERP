import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { mayDo } from '../../../core/landing';
import { localizedName } from '../../../core/multilingual';
import { DialogComponent } from '../../../ui/dialog.component';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { formatQty } from '../backoffice-format';
import { PageChrome } from '../shell/page-chrome';
import { MaintenanceApi, MaintenanceJob } from './maintenance-api.service';
import { MaintenanceJobCostComponent } from './maintenance-job-cost.component';
import { MaintenanceJobHoursComponent } from './maintenance-job-hours.component';
import { MaintenanceJobPartsComponent } from './maintenance-job-parts.component';
import { MaintenanceJobPriceComponent } from './maintenance-job-price.component';
import { maintenanceError } from './maintenance-words';

/**
 * Maintenance › Job (Phase M1, docs/maintenance/plan.md §2.2 and §7): the job's number, kind, status in words, branch and
 * the return note it repairs; its units — taken from what the branch holds in maintenance — and, completed, how many came
 * back repaired and how many went to scrap; its words; its history. A draft (opened by a return note, MS-03) is taken
 * first. Completing says what became of every unit: repaired back to available, beyond repair to scrap (Dr 318 / Cr 310,
 * as the manual state move); under MS-05 someone other than the opener completes it. Cancelling moves nothing. Its hours
 * (Phase M3) are their own card, `app-maintenance-job-hours`, and its cost (Phase M4) `app-maintenance-job-cost`.
 */
@Component({
  selector: 'app-maintenance-job',
  standalone: true,
  imports: [FormsModule, RouterLink, DialogComponent, MaintenanceJobCostComponent, MaintenanceJobHoursComponent, MaintenanceJobPartsComponent, MaintenanceJobPriceComponent, StatusComponent, TableStateComponent],
  template: `
    <div class="bo-stack">
      @if (loading() && !job()) {
        <section class="ui-surface"><app-table-state kind="loading" /></section>
      } @else if (!job() && error()) {
        <section class="ui-surface"><app-table-state kind="error" [message]="error()" (retry)="load()" /></section>
      } @else if (job(); as j) {
        @if (error()) {
          <p class="notice danger" role="alert">{{ error() }}</p>
        }
        @if (done()) {
          <p class="notice ok" role="status">{{ done() }}</p>
        }

        <section class="ui-surface">
          <div class="ui-surface__head">
            <h2>{{ i18n.t('maint.job.summary') }}</h2>
            <app-status [tone]="tone()">{{ i18n.t('maint.status.' + j.status) }}</app-status>
          </div>
          <dl class="ui-surface__body ui-facts">
            <div>
              <dt>{{ i18n.t('maint.job.kind') }}</dt>
              <dd>{{ i18n.t('maint.kind.' + j.kind) }}</dd>
            </div>
            <div>
              <dt>{{ i18n.t('maint.job.branch') }}</dt>
              <dd>{{ j.branchName }}</dd>
            </div>
            @if (j.kind === 'Customer') {
              <!-- Phase M5a: an outside customer's equipment, never company stock. -->
              <div>
                <dt>{{ i18n.t('maint.customer.customer') }}</dt>
                <dd>{{ j.customerName ?? '—' }}</dd>
              </div>
              <div>
                <dt>{{ i18n.t('maint.customer.equipment') }}</dt>
                <dd>{{ j.equipment ?? '—' }}</dd>
              </div>
              <!-- Phase M7c (MS-27, MS-37): the job a free rework redoes; a customer job's guarantee, provision and reworks. -->
              @if (j.reworkOfJobId) {
                <div>
                  <dt>{{ i18n.t('maint.rework.of') }}</dt>
                  <dd>
                    <a [routerLink]="['/backoffice/maintenance/jobs', j.reworkOfJobId]"><span class="mono" dir="ltr">{{ j.reworkOfNumber }}</span></a>
                    <span class="ui-sub">{{ i18n.t('maint.rework.freeHint') }}</span>
                  </dd>
                </div>
              } @else if (j.guaranteeUntil) {
                <div>
                  <dt>{{ i18n.t('maint.rework.guaranteeUntil') }}</dt>
                  <dd class="mono">{{ i18n.date(j.guaranteeUntil) }}</dd>
                </div>
              }
              @if (j.provisionAmount !== null && j.provisionAmount !== undefined) {
                <div>
                  <dt>{{ i18n.t('maint.rework.provision') }}</dt>
                  <dd class="mono">{{ i18n.money(j.provisionAmount) }}</dd>
                </div>
              }
              @if (j.reworks?.length) {
                <div>
                  <dt>{{ i18n.t('maint.rework.list') }}</dt>
                  <dd>
                    @for (r of j.reworks!; track r.id; let last = $last) {
                      <a [routerLink]="['/backoffice/maintenance/jobs', r.id]"><span class="mono" dir="ltr">{{ r.number }}</span></a>
                      <span class="ui-sub">{{ i18n.t('maint.status.' + r.status) }}</span>{{ last ? '' : ' · ' }}
                    }
                  </dd>
                </div>
              }
            }
            @if (j.productId) {
              <div>
                <dt>{{ i18n.t('maint.job.product') }}</dt>
                <dd>{{ productName() }} <span class="ui-sub mono" dir="ltr">{{ j.productSku }}</span></dd>
              </div>
            }
            @if (j.contractId) {
              <div>
                <dt>{{ i18n.t('maint.job.returnNote') }}</dt>
                <dd>
                  <a [routerLink]="['/backoffice/rentals/contracts', j.contractId]"><span dir="ltr">{{ j.contractNumber }}</span></a>
                  · <span class="mono" dir="ltr">{{ j.returnNoteNumber }}</span>
                  @if (j.customerName) {
                    <span class="ui-sub">{{ j.customerName }}</span>
                  }
                </dd>
              </div>
              @if (j.lineDamagedQty !== null) {
                <div>
                  <dt>{{ i18n.t('maint.job.recharge') }}</dt>
                  <dd>
                    <span class="mono">{{ i18n.money(j.lineRecharge ?? 0) }}</span>
                    <span class="ui-sub">{{ i18n.t('maint.job.lineDamaged', { n: qty(j.lineDamagedQty) }) }}</span>
                  </dd>
                </div>
              }
            }
            <div>
              <dt>{{ i18n.t('maint.job.opened') }}</dt>
              <dd>
                @if (j.openedAtUtc) {
                  {{ i18n.t('maint.job.byOn', { who: j.openedBy ?? '—', date: i18n.dateTime(j.openedAtUtc) }) }}
                } @else {
                  {{ i18n.t('maint.job.draftedBy', { who: j.createdBy ?? '—', date: i18n.dateTime(j.createdAtUtc) }) }}
                }
              </dd>
            </div>
            @if (j.completedAtUtc) {
              <div>
                <dt>{{ i18n.t('maint.job.completed') }}</dt>
                <dd>{{ i18n.t('maint.job.byOn', { who: j.completedBy ?? '—', date: i18n.dateTime(j.completedAtUtc) }) }}</dd>
              </div>
            }
            @if (j.cancelledAtUtc) {
              <div>
                <dt>{{ i18n.t('maint.job.cancelled') }}</dt>
                <dd>
                  {{ i18n.t('maint.job.byOn', { who: j.cancelledBy ?? '—', date: i18n.dateTime(j.cancelledAtUtc) }) }}
                  <span class="ui-sub">{{ j.cancelReason }}</span>
                </dd>
              </div>
            }
          </dl>
        </section>

        <section class="ui-surface">
          <div class="ui-surface__head">
            <h2>{{ i18n.t('maint.job.units') }}</h2>
          </div>
          <dl class="ui-surface__body ui-facts">
            <div>
              <dt>{{ i18n.t('maint.job.qty') }}</dt>
              <dd class="mono"><span dir="ltr">{{ qty(j.qty) }}</span></dd>
            </div>
            @if (j.kind === 'Customer') {
              <div>
                <dt>{{ i18n.t('maint.customer.ownership') }}</dt>
                <dd>{{ i18n.t('maint.customer.notStock') }}</dd>
              </div>
            }
            @if (j.status === 'Completed' || j.status === 'Invoiced') {
              <div>
                <dt>{{ i18n.t('maint.job.repaired') }}</dt>
                <dd class="mono"><span dir="ltr">{{ qty(j.repairedQty) }}</span> <span class="ui-sub">{{ i18n.t(j.kind === 'Customer' ? 'maint.customer.repairedWhere' : 'maint.job.repairedWhere') }}</span></dd>
              </div>
              <div>
                <dt>{{ i18n.t('maint.job.scrapped') }}</dt>
                <dd class="mono"><span dir="ltr">{{ qty(j.scrappedQty) }}</span> <span class="ui-sub">{{ i18n.t(j.kind === 'Customer' ? 'maint.customer.scrappedWhere' : 'maint.job.scrappedWhere') }}</span></dd>
              </div>
            } @else if ((j.status === 'Draft' || j.status === 'Open') && j.kind !== 'Customer') {
              <div>
                <dt>{{ i18n.t('maint.job.inMaintenance') }}</dt>
                <dd class="mono"><span dir="ltr">{{ qty(j.inMaintenanceAtBranch) }}</span> <span class="ui-sub">{{ i18n.t('maint.job.inMaintenanceNote') }}</span></dd>
              </div>
            }
          </dl>
        </section>

        <!-- Phase M2: parts issued and returned, outside work. -->
        <app-maintenance-job-parts [job]="j" (changed)="partsAtJob.set($event.partsAtJobQty)" />
        <!-- Phase M3: the hours booked on the job, per person and day, under the one attendance ceiling. -->
        <app-maintenance-job-hours [job]="j" />
        <!-- Phase M4: what the job cost, provisional or final in words; labour money only with pay-rate access. -->
        @if (canSeeCost()) {
          <app-maintenance-job-cost [job]="j" />
        }
        <!-- Phase M5a: a customer job's price, VAT on request, and its one invoice. -->
        @if (j.kind === 'Customer' && !j.reworkOfJobId) {
          <app-maintenance-job-price [job]="j" (invoiced)="show($event)" />
        } @else if (j.reworkOfJobId) {
          <!-- Phase M7c: a rework under guarantee is free, never priced nor invoiced. -->
          <p class="notice info" role="note">{{ i18n.t('maint.rework.freeNotice', { number: j.reworkOfNumber ?? '' }) }}</p>
        }

        <section class="bo-panel bo-panel--operational">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('maint.job.words') }}</h2>
          </div>
          <div class="bo-panel__body bo-form">
            @if (j.kind === 'Customer') {
              <div class="bo-field">
                <label for="mj-equipment">{{ i18n.t('maint.customer.equipment') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                <input id="mj-equipment" maxlength="200" [(ngModel)]="words.equipment" [disabled]="!editable()" />
              </div>
            }
            <div class="bo-field">
              <label for="mj-ref">{{ i18n.t('maint.job.itemReference') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
              <input id="mj-ref" maxlength="120" [(ngModel)]="words.itemReference" [disabled]="!editable()" />
            </div>
            <div class="bo-field">
              <label for="mj-desc">{{ i18n.t('maint.job.description') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
              <textarea id="mj-desc" rows="2" maxlength="1000" [(ngModel)]="words.description" [disabled]="!editable()"></textarea>
            </div>
            <div class="bo-field">
              <label for="mj-diag">{{ i18n.t('maint.job.diagnosis') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
              <textarea id="mj-diag" rows="3" maxlength="2000" [(ngModel)]="words.diagnosis" [disabled]="!editable()"></textarea>
            </div>
            @if (editable()) {
              <div>
                <button type="button" class="btn-ghost" [disabled]="busy()" (click)="saveWords()">{{ i18n.t('maint.job.saveWords') }}</button>
              </div>
            }
          </div>
        </section>

        @if (j.status === 'Open' && canClose()) {
          <section class="bo-panel bo-panel--operational" aria-labelledby="mj-complete-title">
            <div class="bo-panel__head">
              <h2 class="bo-panel__title" id="mj-complete-title">{{ i18n.t('maint.job.completeHeading') }}</h2>
              <p class="bo-panel__note">{{ i18n.t('maint.job.completeLead', { n: qty(j.qty) }) }}</p>
            </div>
            <div class="bo-panel__body bo-form">
              @if (ownJob()) {
                <p class="notice warn" role="status">{{ i18n.t('maint.job.fourEyes') }}</p>
              }
              <div class="bo-field">
                <label for="mj-repaired">{{ i18n.t('maint.job.repaired') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                <input id="mj-repaired" type="number" min="0" step="any" class="num mono" [(ngModel)]="repaired" (ngModelChange)="scrapped = remainder($event)" />
                <p class="bo-hint">{{ i18n.t('maint.job.repairedWhere') }}</p>
              </div>
              <div class="bo-field">
                <label for="mj-scrapped">{{ i18n.t('maint.job.scrapped') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                <input id="mj-scrapped" type="number" min="0" step="any" class="num mono" [(ngModel)]="scrapped" />
                <p class="bo-hint">{{ i18n.t('maint.job.scrappedWhere') }}</p>
              </div>
              @if (!balanced()) {
                <p class="notice warn" role="status">{{ i18n.t('maint.job.mustBalance', { n: qty(j.qty) }) }}</p>
              }
              @if (partsAtJob() > 0) {
                <p class="bo-hint">{{ i18n.t('maint.parts.usedAtCompletion', { n: qty(partsAtJob()) }) }}</p>
              }
            </div>
          </section>
        }

        <section class="ui-surface">
          <div class="ui-surface__head">
            <h2>{{ i18n.t('maint.job.history') }}</h2>
          </div>
          <ol class="ui-surface__body history">
            @for (e of j.events; track $index) {
              <li>
                <span class="mono">{{ i18n.dateTime(e.atUtc) }}</span>
                {{ i18n.t('maint.event.' + e.kind) }} — {{ e.userName ?? '—' }}
                @if (e.detail) {
                  <span class="ui-sub">{{ e.detail }}</span>
                }
              </li>
            }
          </ol>
        </section>

        @if (actionable()) {
          <div class="ui-actionbar ui-actionbar--sticky">
            @if (canClose() && (j.status === 'Draft' || j.status === 'Open')) {
              <button type="button" class="btn-ghost danger-text" [disabled]="busy()" (click)="askCancel()">{{ i18n.t('maint.job.cancel') }}</button>
            }
            @if (j.status === 'Draft' && canManage()) {
              <button type="button" class="btn-primary" [disabled]="busy()" (click)="take()">{{ i18n.t('maint.job.take') }}</button>
            }
            @if (j.status === 'Open' && canClose()) {
              <button type="button" class="btn-primary" [disabled]="busy() || !balanced() || ownJob()" (click)="complete()">{{ i18n.t('maint.job.complete') }}</button>
            }
          </div>
        }
      }
    </div>

    <app-dialog [open]="cancelling()" size="sm" [heading]="i18n.t('maint.job.cancelHeading', { n: job()?.number ?? '' })" (dismiss)="cancelling.set(false)">
      @if (cancelling()) {
        <div class="bo-form">
          <p class="bo-hint">{{ i18n.t('maint.job.cancelNote') }}</p>
          <div class="bo-field">
            <label for="mj-reason">{{ i18n.t('maint.job.cancelReason') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
            <input id="mj-reason" maxlength="400" [(ngModel)]="cancelReason" />
          </div>
          @if (partsAtJob() > 0) {
            <p class="notice warn" role="status">{{ i18n.t('maint.parts.atJobOnCancel', { n: qty(partsAtJob()) }) }}</p>
            <label class="bo-check">
              <input id="mj-parts-used" type="checkbox" [(ngModel)]="partsUsed" />
              {{ i18n.t('maint.parts.keepAsUsed') }}
            </label>
          }
        </div>
      }
      <div dialogFoot class="ui-actionbar">
        <button type="button" class="btn-ghost" (click)="cancelling.set(false)">{{ i18n.t('common.close') }}</button>
        <button type="button" class="btn-danger" [disabled]="busy() || !cancelReason.trim() || (partsAtJob() > 0 && !partsUsed)" (click)="cancel()">
          {{ i18n.t('maint.job.cancel') }}
        </button>
      </div>
    </app-dialog>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .history {
        display: grid;
        gap: var(--sp-2);
        margin: 0;
        padding-inline-start: var(--sp-5);
      }
      .danger-text {
        color: var(--danger);
        margin-inline-end: auto;
      }
    `,
  ],
})
export class MaintenanceJobComponent implements OnInit {
  private readonly api = inject(MaintenanceApi);
  private readonly auth = inject(AuthService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  readonly id = input.required<string>();

  readonly job = signal<MaintenanceJob | null>(null);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly done = signal<string | null>(null);
  readonly cancelling = signal(false);
  /** What is still at the job of the parts issued to it (Phase M2), as the Parts card last read it. */
  readonly partsAtJob = signal(0);
  cancelReason = '';
  /** Cancelling with parts still at the job: they stay as used (plan §2.3), or they are returned first. */
  partsUsed = false;
  words = { itemReference: '', description: '', diagnosis: '', equipment: '' };
  repaired: number | null = 0;
  scrapped: number | null = 0;

  readonly canManage = computed(() => mayDo(this.auth.currentUser(), 'maintenance.jobs.manage'));
  readonly canClose = computed(() => mayDo(this.auth.currentUser(), 'maintenance.jobs.close'));
  /** The Cost card (Phase M4): maintenance.costs.view; the server decides what of the labour money it shows. */
  readonly canSeeCost = computed(() => mayDo(this.auth.currentUser(), 'maintenance.costs.view'));
  readonly tone = computed(() => toneOf('maintenanceJob', this.job()?.status));
  readonly productName = computed(() => {
    const j = this.job();
    return j ? localizedName({ nameAr: j.productNameAr, nameFr: j.productNameFr, nameEn: j.productNameEn }, this.i18n.lang()) || j.productSku : '';
  });
  readonly editable = computed(() => {
    const status = this.job()?.status;
    return this.canManage() && (status === 'Draft' || status === 'Open');
  });
  /** MS-05 "not the opener": the person who opened the job does not complete it; the server refuses it too. */
  readonly ownJob = computed(() => {
    const j = this.job();
    const me = (this.auth.currentUser() as { id?: string } | null)?.id;
    return !!j && j.jobCloser === 'NotTheOpener' && !!j.openedByUserId && j.openedByUserId === me;
  });
  readonly actionable = computed(() => {
    const status = this.job()?.status;
    return (status === 'Draft' && (this.canManage() || this.canClose())) || (status === 'Open' && this.canClose());
  });

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.show(await this.api.job(this.id()));
    } catch (e) {
      this.error.set(maintenanceError(e, (k) => this.i18n.t(k)));
    } finally {
      this.loading.set(false);
    }
  }

  qty(value: number | null | undefined): string {
    return formatQty(value ?? 0, this.i18n.lang());
  }

  /** What is left of the job's units once the repaired ones are counted: proposed as beyond repair. */
  remainder(repaired: number | null): number {
    const total = this.job()?.qty ?? 0;
    return Math.max(0, total - (Number(repaired) || 0));
  }

  balanced(): boolean {
    const j = this.job();
    if (!j) return false;
    const r = Number(this.repaired);
    const s = Number(this.scrapped);
    return Number.isFinite(r) && Number.isFinite(s) && r >= 0 && s >= 0 && Math.abs(r + s - j.qty) < 1e-9;
  }

  async saveWords(): Promise<void> {
    await this.act(() => this.api.update(this.id(), this.trimmedWords()), 'maint.job.wordsSaved');
  }

  async take(): Promise<void> {
    await this.act(() => this.api.open(this.id()), 'maint.job.taken');
  }

  async complete(): Promise<void> {
    await this.act(
      () => this.api.complete(this.id(), Number(this.repaired), Number(this.scrapped), this.trimmedWords().diagnosis),
      'maint.job.completedNotice',
    );
  }

  askCancel(): void {
    this.cancelReason = '';
    this.partsUsed = false;
    this.cancelling.set(true);
  }

  async cancel(): Promise<void> {
    this.cancelling.set(false);
    await this.act(() => this.api.cancel(this.id(), this.cancelReason.trim(), this.partsAtJob() > 0 && this.partsUsed), 'maint.job.cancelledNotice');
  }

  private trimmedWords() {
    const clean = (v: string) => v.trim() || null;
    return {
      itemReference: clean(this.words.itemReference), description: clean(this.words.description), diagnosis: clean(this.words.diagnosis),
      equipment: this.job()?.kind === 'Customer' ? clean(this.words.equipment) : null,
    };
  }

  private async act(call: () => Promise<MaintenanceJob>, notice: string): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.done.set(null);
    try {
      this.show(await call());
      this.done.set(this.i18n.t(notice));
    } catch (e) {
      this.error.set(maintenanceError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }

  show(j: MaintenanceJob): void {
    this.job.set(j);
    this.words = { itemReference: j.itemReference ?? '', description: j.description ?? '', diagnosis: j.diagnosis ?? '', equipment: j.equipment ?? '' };
    this.repaired = j.qty;
    this.scrapped = 0;
    this.chrome?.heading.set(`${j.number} — ${j.kind === 'Customer' ? (j.equipment ?? '') : this.productName()}`);
    this.chrome?.description.set(null);
  }
}
