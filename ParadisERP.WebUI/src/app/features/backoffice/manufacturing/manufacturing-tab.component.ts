import { Component, computed, effect, inject, input, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../../core/auth.service';
import { PageChrome } from '../shell/page-chrome';
import { MfgBomEditorComponent } from './bom-editor.component';
import { MfgBomsComponent } from './boms.component';
import { MfgCostReconciliationComponent } from './cost-reconciliation.component';
import { MfgCostingSettingsComponent } from './costing-settings.component';
import { MfgJobRolesComponent } from './job-roles.component';
import { MfgLabourCostComponent } from './labour-cost.component';
import { MFG, mfgCan } from './manufacturing-shared';
import { MfgMaterialFlowComponent } from './material-flow.component';
import { MfgMaterialsComponent } from './materials.component';
import { MfgOrderComponent } from './order.component';
import { MfgOrdersComponent } from './orders.component';
import { MfgPendingMovementsComponent } from './pending-movements.component';
import { MfgPosExceptionsComponent } from './pos-exceptions.component';
import { MfgPracticeLedgerComponent } from './practice-ledger.component';
import { MfgRecordComponent } from './record.component';
import { MfgRecordsComponent } from './records.component';
import { MfgSpecFieldsComponent } from './spec-fields.component';
import { MfgLineComponent } from './production-line.component';
import { MfgLinesComponent } from './production-lines.component';
import { MfgStageTemplatesComponent } from './stage-templates.component';
import { MfgUnitsComponent } from './units.component';
import { MfgWorkInProgressComponent } from './work-in-progress.component';

export type ManufacturingPage =
  | 'lines' | 'orders' | 'records' | 'boms' | 'materials' | 'stages' | 'jobRoles' | 'units' | 'specs'
  | 'posExceptions' | 'pending' | 'materialFlow' | 'workInProgress' | 'costing' | 'labourCost' | 'costingSettings' | 'practiceLedger';

/**
 * The manufacturing module (Phases 1 and 2): one chunk for its twelve pages, so a till and a back office that
 * never opens it download none of it. Lines and recipes are addresses all the way down —
 * `manufacturing/lines/<id>`, `manufacturing/boms/<id>`, `…/boms/new?line=<id>` — read from the address,
 * never held beside it, so a reload, a deep link, Back and Forward land where the address says.
 *
 * What a page offers follows the capabilities the login answered; the server decides again on every
 * request, and decides alone which lines a reader reaches.
 */
@Component({
  selector: 'app-backoffice-manufacturing',
  standalone: true,
  imports: [
    MfgBomEditorComponent,
    MfgBomsComponent,
    MfgCostReconciliationComponent,
    MfgCostingSettingsComponent,
    MfgJobRolesComponent,
    MfgLabourCostComponent,
    MfgLineComponent,
    MfgLinesComponent,
    MfgMaterialFlowComponent,
    MfgMaterialsComponent,
    MfgOrderComponent,
    MfgOrdersComponent,
    MfgPendingMovementsComponent,
    MfgPosExceptionsComponent,
    MfgPracticeLedgerComponent,
    MfgRecordComponent,
    MfgRecordsComponent,
    MfgSpecFieldsComponent,
    MfgStageTemplatesComponent,
    MfgUnitsComponent,
    MfgWorkInProgressComponent,
  ],
  template: `
    @switch (page()) {
      @case ('lines') {
        @if (path()[0]; as id) {
          <!-- Keyed on the line: another line's address is another screen. -->
          @for (key of [id]; track key) {
            <app-mfg-line [id]="key" (openRecipes)="go(['boms'], { line: $event })" />
          }
        } @else {
          <app-mfg-lines (openLine)="go(['lines', $event])" />
        }
      }
      @case ('boms') {
        @if (path()[0]; as id) {
          @for (key of [id]; track key) {
            <app-mfg-bom-editor
              [id]="key === 'new' ? null : key"
              [startLine]="line()"
              [mayWrite]="can.bom()"
              (opened)="go(['boms', $event], null, key === 'new')"
              (cancelled)="back()"
              (deleted)="go(['boms'], { line: line() })"
            />
          }
        } @else {
          <app-mfg-boms
            [lineFilter]="line()"
            [canWrite]="can.bom()"
            (open)="go(['boms', $event])"
            (create)="go(['boms', 'new'], { line: $event })"
            (lineFilterChange)="go(['boms'], { line: $event }, true)"
          />
        }
      }
      @case ('orders') {
        @if (path()[0]; as id) {
          @for (key of [id]; track key) {
            <app-mfg-order [id]="key" />
          }
        } @else {
          <app-mfg-orders [lineFilter]="line()" (open)="go(['orders', $event])" (lineFilterChange)="go(['orders'], { line: $event }, true)" />
        }
      }
      @case ('records') {
        @if (path()[0]; as id) {
          @for (key of [id]; track key) {
            <app-mfg-record [id]="key" />
          }
        } @else {
          <app-mfg-records [lineFilter]="line()" (open)="go(['records', $event])" (lineFilterChange)="go(['records'], { line: $event }, true)" />
        }
      }
      @case ('posExceptions') { <app-mfg-pos-exceptions /> }
      @case ('pending') { <app-mfg-pending-movements /> }
      @case ('materialFlow') { <app-mfg-material-flow /> }
      @case ('workInProgress') { <app-mfg-work-in-progress (open)="go(['orders', $event])" /> }
      @case ('costing') { <app-mfg-cost-reconciliation (open)="go(['orders', $event])" /> }
      @case ('labourCost') { <app-mfg-labour-cost /> }
      @case ('costingSettings') { <app-mfg-costing-settings /> }
      @case ('practiceLedger') { <app-mfg-practice-ledger /> }
      @case ('materials') { <app-mfg-materials [canEdit]="can.settings()" /> }
      @case ('stages') { <app-mfg-stage-templates [canEdit]="can.settings()" /> }
      @case ('jobRoles') { <app-mfg-job-roles [canEdit]="can.settings()" /> }
      @case ('units') { <app-mfg-units [canEdit]="can.settings()" /> }
      @case ('specs') { <app-mfg-spec-fields [canEdit]="can.settings()" /> }
    }
  `,
})
export class BackofficeManufacturingComponent {
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly chrome = inject(PageChrome, { optional: true });

  readonly page = input.required<ManufacturingPage>();
  /** The address under the page (core/nav.ts `deep`): `[]`, `[id]` or `['new']`. */
  readonly path = input<string[]>([]);

  private readonly query = toSignal(this.route.queryParamMap, { initialValue: this.route.snapshot.queryParamMap });
  readonly line = computed(() => this.query().get('line'));

  readonly can = {
    bom: computed(() => mfgCan(this.auth.currentUser(), MFG.bom)),
    settings: computed(() => mfgCan(this.auth.currentUser(), MFG.settings)),
  };

  constructor() {
    // A list is the page itself: whatever a record set in the header is cleared on the way back.
    effect(() => {
      const atList = !this.path().length;
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
    void this.router.navigate(['/backoffice', 'manufacturing', ...segments], { queryParams, replaceUrl: replace });
  }

  back(): void {
    if (history.length > 1) history.back();
    else this.go([this.page() === 'boms' ? 'boms' : 'lines']);
  }
}
