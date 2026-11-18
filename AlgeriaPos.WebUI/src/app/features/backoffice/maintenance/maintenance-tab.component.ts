import { Component, computed, effect, inject, input, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { PageChrome } from '../shell/page-chrome';
import { MaintenanceHistoryComponent } from './maintenance-history.component';
import { MaintenanceJobNewComponent } from './maintenance-job-new.component';
import { MaintenanceJobComponent } from './maintenance-job.component';
import { MaintenanceJobsComponent } from './maintenance-jobs.component';
import { MaintenanceRepairRatesComponent } from './maintenance-repair-rates.component';
import { MaintenancePracticeLedgerComponent } from './maintenance-practice-ledger.component';
import { MaintenanceReportsComponent } from './maintenance-reports.component';
import { MaintenanceSettingsComponent } from './maintenance-settings.component';
import { MaintenanceWaitingComponent } from './maintenance-waiting.component';
import { MaintenanceWorkshopComponent } from './maintenance-workshop.component';

export type MaintenancePage = 'jobs' | 'waiting' | 'history' | 'reports' | 'rates' | 'practiceLedger' | 'workshop' | 'settings';

/**
 * The maintenance module (docs/maintenance/plan.md §7): one chunk for its pages. Jobs are addresses all the way down —
 * `maintenance/jobs/<id>`, `maintenance/jobs/new?line=<return-note line>` or `?product=<id>&branch=<id>` — read from the
 * address, never held beside it, so a reload, a deep link, Back and Forward land where the address says.
 */
@Component({
  selector: 'app-backoffice-maintenance',
  standalone: true,
  imports: [
    MaintenanceJobsComponent, MaintenanceJobComponent, MaintenanceJobNewComponent, MaintenanceReportsComponent, MaintenanceSettingsComponent,
    MaintenanceWaitingComponent, MaintenancePracticeLedgerComponent, MaintenanceRepairRatesComponent, MaintenanceHistoryComponent,
    MaintenanceWorkshopComponent,
  ],
  template: `
    @switch (page()) {
      @case ('jobs') {
        @if (path()[0]; as id) {
          <!-- Keyed on the address: another job is another screen. -->
          @for (key of [id]; track key) {
            @if (key === 'new') {
              <app-maintenance-job-new
                [line]="query().get('line')"
                [product]="query().get('product')"
                [branch]="query().get('branch')"
                (opened)="go(['jobs', $event], null, true)"
                (cancelled)="back()"
              />
            } @else {
              <app-maintenance-job [id]="key" />
            }
          }
        } @else {
          <app-maintenance-jobs (open)="go(['jobs', $event])" (create)="go(['jobs', 'new'])" />
        }
      }
      @case ('waiting') {
        <app-maintenance-waiting
          (openLine)="go(['jobs', 'new'], { line: $event })"
          (openBucket)="go(['jobs', 'new'], { product: $event.productId, branch: $event.branchId })"
        />
      }
      @case ('history') {
        <!-- Phase M7d (MS-38): the product and the piece are the address, so Back and Forward follow them. -->
        <app-maintenance-history
          [product]="query().get('product')"
          [reference]="query().get('reference')"
          (pick)="go(['history'], { product: $event.product, reference: $event.reference })"
          (openJob)="go(['jobs', $event])"
        />
      }
      @case ('reports') { <app-maintenance-reports (openJob)="go(['jobs', $event])" /> }
      @case ('rates') { <app-maintenance-repair-rates /> }
      @case ('practiceLedger') { <app-maintenance-practice-ledger /> }
      @case ('workshop') { <app-maintenance-workshop /> }
      @case ('settings') { <app-maintenance-settings /> }
    }
  `,
})
export class BackofficeMaintenanceComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly chrome = inject(PageChrome, { optional: true });

  readonly page = input.required<MaintenancePage>();
  /** The address under the page (core/nav.ts `deep`): `[]`, `[id]` or `['new']`. */
  readonly path = input<string[]>([]);

  readonly query = toSignal(this.route.queryParamMap, { initialValue: this.route.snapshot.queryParamMap });
  readonly atList = computed(() => !this.path().length);

  constructor() {
    // A list is the page itself: whatever a job set in the header is cleared on the way back.
    effect(() => {
      const atList = this.atList();
      untracked(() => {
        if (atList) {
          this.chrome?.heading.set(null);
          this.chrome?.trail.set([]);
        }
      });
    });
  }

  go(segments: string[], query: Record<string, string | null> | null = null, replace = false): void {
    const queryParams = Object.fromEntries(Object.entries(query ?? {}).filter(([, v]) => !!v));
    void this.router.navigate(['/backoffice', 'maintenance', ...segments], { queryParams, replaceUrl: replace });
  }

  back(): void {
    if (history.length > 1) history.back();
    else this.go(['jobs']);
  }
}
