import { NgTemplateOutlet } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { adminErrorMessage, problemCode } from '../../../core/admin.service';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { mayDo } from '../../../core/landing';
import { LedgerAccount, LedgerService } from '../../../core/ledger.service';
import { localizedName } from '../../../core/multilingual';
import { MaintenanceModule } from '../../../core/maintenance-module.service';
import { FoldComponent } from '../../../ui/fold.component';
import { TableStateComponent } from '../../../ui/table-state.component';
import { MonthInputComponent } from '../payroll/month-input.component';
import { ReceivablesApi } from '../receivables/receivables-api.service';
import { PageChrome } from '../shell/page-chrome';
import { MaintenanceApi, MaintenanceSettings, MaintenanceSettingsDraft } from './maintenance-api.service';
import { MaintenanceAccountSlot, MaintenanceLedgerApi } from './maintenance-ledger-api.service';
import { MaintenanceWorkshop, MaintenanceWorkshopApi, emptyWorkshopBranches } from './maintenance-workshop-api.service';

type ChoiceField = {
  [K in keyof MaintenanceSettingsDraft]: MaintenanceSettingsDraft[K] extends string ? K : never;
}[keyof MaintenanceSettingsDraft];

/** One choice on the page: its field, the plan's number, its options (the first is the suggested default) and those kept for later. */
interface Choice {
  field: Exclude<ChoiceField, 'postingFromMonth'>;
  ms: string;
  options: readonly string[];
  /** Known to the server but refused until a later phase. */
  later?: readonly string[];
}

/** The refusals the page says in the reader's language; anything else falls back to the server's sentence. */
const REFUSALS: Readonly<Record<string, string>> = {
  maintenance_settings_required: 'maint.error.operational',
  accounting_manage_required: 'maint.error.accounting',
  maintenance_option_not_yet: 'maint.error.later',
  accounts_not_mapped: 'maint.error.accountsNotMapped',
  register_not_active: 'maint.error.registerNotActive',
  posting_off: 'maint.error.postingOff',
};

/**
 * Maintenance › Settings (Phase M0, docs/maintenance/plan.md §4 and §7). Every maintenance decision is a setting the
 * admin or the accountant sets here (owner, 2026-11-08/09): the first option of each is the default and the suggested
 * answer, and every default keeps today's behaviour — the module is off, nothing posts, rentals and stock unchanged.
 * Module and jobs, Cost and Pricing are `maintenance.settings`'s (MS-01 to MS-27); Accounting is `accounting.manage`'s
 * (MS-30 to MS-40), with the accountant's confirmation, and so is the account map's maintenance slots (MS-31, Phase M6),
 * saved on their own route.
 */
@Component({
  selector: 'app-maintenance-settings',
  standalone: true,
  imports: [FormsModule, NgTemplateOutlet, FoldComponent, MonthInputComponent, TableStateComponent],
  template: `
    <div class="bo-stack">
      @if (loading() && !settings()) {
        <section class="ui-surface"><app-table-state kind="loading" /></section>
      } @else if (settings(); as s) {
        <!-- The module's state in words, beside nothing that carries it in colour alone. -->
        <p class="notice" [class.info]="s.module === 'Off'" [class.ok]="s.module === 'On'" role="status">
          {{ i18n.t(s.module === 'On' ? 'maint.state.on' : 'maint.state.off') }}
        </p>
        @if (error()) {
          <p class="notice danger" role="alert">{{ error() }}</p>
        }
        @if (saved()) {
          <p class="notice ok" role="status">{{ i18n.t('maint.saved') }}</p>
        }

        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('maint.title') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('maint.lead') }}</p>
          </div>

          <!-- The first section opens: it holds the module. The others start folded until the work reaches them. -->
          <div class="bo-panel__body bo-form bo-form--full">
            <app-fold class="bo-group bo-group--row" [heading]="i18n.t('maint.section.module')" [open]="true">
              <p class="bo-group__note" foldLead>{{ i18n.t('maint.section.module.lead') }}</p>
              <div class="bo-fields bo-fields--roomy">
                @for (c of moduleChoices; track c.field) {
                  <ng-container *ngTemplateOutlet="choice; context: { $implicit: c, editable: canOperate() }" />
                  @if (c.field === 'bookableEmployees' && draft.bookableEmployees === 'WorkshopListOnly') {
                    <!-- MS-04: the list is Maintenance › Workshop's; taken even while a branch's list is empty, which is said here. -->
                    <div class="bo-field">
                      <p class="bo-hint">MS-04 · {{ i18n.t('maint.workshop.settingsHint') }}</p>
                      @if (emptyBranches()) {
                        <p class="notice warn" role="status">{{ i18n.t('maint.workshop.settingsEmpty', { branches: emptyBranches() }) }}</p>
                      }
                    </div>
                  }
                }
                <!-- MS-27 (Phase M7c): the guarantee a customer job carries; a free rework is linked within it. -->
                <div class="bo-field">
                  <label for="ms-guarantee">{{ i18n.t('maint.guaranteeMonths') }}</label>
                  <input id="ms-guarantee" type="number" min="0" max="60" step="1" class="num mono" [(ngModel)]="draft.guaranteeMonths"
                    [disabled]="!canOperate() || busy()" aria-describedby="ms-guarantee-help" />
                  <p id="ms-guarantee-help" class="bo-hint">MS-27 · {{ i18n.t('maint.guaranteeMonths.help') }}</p>
                </div>
              </div>
            </app-fold>

            <app-fold class="bo-group bo-group--row" [heading]="i18n.t('maint.section.cost')">
              <p class="bo-group__note" foldLead>{{ i18n.t('maint.section.cost.lead') }}</p>
              <div class="bo-fields bo-fields--roomy">
                <ng-container *ngTemplateOutlet="choice; context: { $implicit: costChoices[0], editable: canOperate() }" />
                @if (draft.costIncludes === 'PartsLabourOutsideWorkOverhead') {
                  <div class="bo-field">
                    <label for="ms-overhead">{{ i18n.t('maint.overheadPerLabourHour') }} ({{ i18n.t('maint.required') }})</label>
                    <input id="ms-overhead" type="number" min="0" step="0.01" class="num mono" [(ngModel)]="draft.overheadPerLabourHour"
                      [disabled]="!canOperate() || busy()" aria-describedby="ms-overhead-help" />
                    <p id="ms-overhead-help" class="bo-hint">MS-11 · {{ i18n.t('maint.overheadPerLabourHour.help') }}</p>
                  </div>
                }
                <ng-container *ngTemplateOutlet="choice; context: { $implicit: costChoices[1], editable: canOperate() }" />
                @if (draft.labourRate === 'WorkshopRate') {
                  <div class="bo-field">
                    <label for="ms-workshop-rate">{{ i18n.t('maint.workshopRatePerHour') }} ({{ i18n.t('maint.required') }})</label>
                    <input id="ms-workshop-rate" type="number" min="0" step="0.01" class="num mono" [(ngModel)]="draft.workshopRatePerHour"
                      [disabled]="!canOperate() || busy()" aria-describedby="ms-workshop-rate-help" />
                    <p id="ms-workshop-rate-help" class="bo-hint">MS-12 · {{ i18n.t('maint.workshopRatePerHour.help') }}</p>
                  </div>
                }
                <ng-container *ngTemplateOutlet="choice; context: { $implicit: costChoices[2], editable: canOperate() }" />
                @if (draft.burdenAndVat === 'OwnValues') {
                  <div class="bo-field">
                    <label for="ms-burden">{{ i18n.t('maint.employerBurdenPercent') }}</label>
                    <input id="ms-burden" type="number" min="0" max="100" step="0.01" class="num mono" [(ngModel)]="draft.employerBurdenPercent"
                      [disabled]="!canOperate() || busy()" />
                  </div>
                  <div class="bo-field">
                    <label for="ms-vat">{{ i18n.t('maint.expenseVatPercent') }}</label>
                    <input id="ms-vat" type="number" min="0" max="100" step="0.01" class="num mono" [(ngModel)]="draft.expenseVatPercent"
                      [disabled]="!canOperate() || busy()" />
                  </div>
                }
              </div>
            </app-fold>

            <app-fold class="bo-group bo-group--row" [heading]="i18n.t('maint.section.pricing')">
              <p class="bo-group__note" foldLead>{{ i18n.t('maint.section.pricing.lead') }}</p>
              <div class="bo-fields bo-fields--roomy">
                <ng-container *ngTemplateOutlet="choice; context: { $implicit: pricingChoices[0], editable: canOperate() }" />
                @if (draft.customerPrice === 'Itemised') {
                  <div class="bo-field">
                    <label for="ms-labour-charge">{{ i18n.t('maint.labourChargePerHour') }}</label>
                    <input id="ms-labour-charge" type="number" min="0" step="0.01" class="num mono" [(ngModel)]="draft.labourChargePerHour"
                      [disabled]="!canOperate() || busy()" aria-describedby="ms-labour-charge-help" />
                    <p id="ms-labour-charge-help" class="bo-hint">MS-21 · {{ i18n.t('maint.labourChargePerHour.help') }}</p>
                  </div>
                }
                @if (draft.customerPrice !== 'Quoted') {
                  <div class="bo-field">
                    <label for="ms-markup">{{ i18n.t('maint.markupPercent') }}</label>
                    <input id="ms-markup" type="number" min="0" max="1000" step="0.01" class="num mono" [(ngModel)]="draft.markupPercent"
                      [disabled]="!canOperate() || busy()" aria-describedby="ms-markup-help" />
                    <p id="ms-markup-help" class="bo-hint">MS-22 · {{ i18n.t('maint.markupPercent.help') }}</p>
                  </div>
                }
                @for (c of pricingChoices.slice(1); track c.field) {
                  <ng-container *ngTemplateOutlet="choice; context: { $implicit: c, editable: canOperate() }" />
                }
              </div>
            </app-fold>

            <app-fold class="bo-group bo-group--row" [heading]="i18n.t('maint.section.accounting')">
              <p class="bo-group__note" foldLead>{{ i18n.t('maint.section.accounting.lead') }}</p>
              <div class="bo-fields bo-fields--roomy">
                @for (c of accountingChoices; track c.field) {
                  <ng-container *ngTemplateOutlet="choice; context: { $implicit: c, editable: canAccount() }" />
                  @if (c.field === 'ownRepairs' && draft.ownRepairs === 'CapitalisedAboveAmount') {
                    <!-- MS-30 (Phase M7d): the cost above which a repair of own hire equipment joins its register pool. -->
                    <div class="bo-field">
                      <label for="ms-capitalise">{{ i18n.t('maint.capitaliseAboveAmount') }} ({{ i18n.t('maint.required') }})</label>
                      <input id="ms-capitalise" type="number" min="0" step="0.01" class="num mono" [(ngModel)]="draft.capitaliseAboveAmount"
                        [disabled]="!canAccount() || busy()" aria-describedby="ms-capitalise-help" />
                      <p id="ms-capitalise-help" class="bo-hint">MS-30 · {{ i18n.t('maint.capitaliseAboveAmount.help') }}</p>
                      @if (!s.assetRegisterActive) {
                        <p class="notice warn" role="status">{{ i18n.t('maint.capitalise.registerOff') }}</p>
                      }
                    </div>
                  }
                  @if (c.field === 'repairHistory' && draft.repairHistory === 'PerPiece') {
                    <!-- MS-38 (Phase M7d): the unit value above which a piece is tracked on its own. -->
                    <div class="bo-field">
                      <label for="ms-per-piece">{{ i18n.t('maint.perPieceAboveValue') }} ({{ i18n.t('maint.required') }})</label>
                      <input id="ms-per-piece" type="number" min="0" step="0.01" class="num mono" [(ngModel)]="draft.perPieceAboveValue"
                        [disabled]="!canAccount() || busy()" aria-describedby="ms-per-piece-help" />
                      <p id="ms-per-piece-help" class="bo-hint">MS-38 · {{ i18n.t('maint.perPieceAboveValue.help') }}</p>
                    </div>
                  }
                  @if (c.field === 'rework' && draft.rework === 'Provision') {
                    <!-- MS-37 "provision" (Phase M7c): the share of each customer job's invoice net set aside for rework. -->
                    <div class="bo-field">
                      <label for="ms-provision">{{ i18n.t('maint.provisionPercent') }} ({{ i18n.t('maint.required') }})</label>
                      <input id="ms-provision" type="number" min="0" max="100" step="0.01" class="num mono" [(ngModel)]="draft.provisionPercent"
                        [disabled]="!canAccount() || busy()" aria-describedby="ms-provision-help" />
                      <p id="ms-provision-help" class="bo-hint">MS-37 · {{ i18n.t('maint.provisionPercent.help') }}</p>
                    </div>
                  }
                }
                <div class="bo-field">
                  <label for="ms-posting-from">{{ i18n.t('maint.postingFromMonth') }}</label>
                  <app-month-input id="ms-posting-from" [(ngModel)]="postingFrom" [disabled]="!canAccount() || busy()" />
                  <p class="bo-hint">MS-39 · {{ i18n.t('maint.postingFromMonth.help') }}</p>
                </div>
                <!-- MS-40: the accountant's confirmation, given here instead of on paper. -->
                <div class="bo-field bo-field--wide">
                  <div class="bo-check">
                    <input id="ms-approved" type="checkbox" [(ngModel)]="draft.accountantApproved" [disabled]="!canAccount() || busy()"
                      aria-describedby="ms-approved-help" />
                    <label for="ms-approved">{{ i18n.t('maint.approval.confirm') }}</label>
                  </div>
                  <p id="ms-approved-help" class="bo-hint">MS-40 · {{ i18n.t('maint.approval.help') }}</p>
                  @if (s.accountantApproved && s.approvedAtUtc) {
                    <span class="ui-sub">{{ i18n.t('maint.approval.by', { who: s.approvedBy ?? '—', date: i18n.dateTime(s.approvedAtUtc) }) }}</span>
                  } @else {
                    <span class="ui-sub">{{ i18n.t('maint.approval.not') }}</span>
                  }
                </div>
              </div>
              @if (!canAccount()) {
                <p class="ui-sub">{{ i18n.t('maint.accountingReadOnly') }}</p>
              }
            </app-fold>

            <!-- MS-31 (Phase M6): the account map's maintenance slots, seeded empty and never guessed; saved on their own. -->
            <app-fold class="bo-group bo-group--row" [heading]="i18n.t('maint.section.accountMap')" [open]="mapOpen()">
              <p class="bo-group__note" foldLead>MS-31 · {{ i18n.t('maint.map.lead') }}</p>
              <div class="ui-scroll bo-field--wide">
                <table class="ui-table ui-table--cards">
                  <caption class="sr-only">{{ i18n.t('maint.section.accountMap') }}</caption>
                  <thead>
                    <tr>
                      <th>{{ i18n.t('mfgChoices.map.slot') }}</th>
                      <th>{{ i18n.t('mfgChoices.map.account') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (m of map(); track m.slot) {
                      <tr>
                        <td [attr.data-label]="i18n.t('mfgChoices.map.slot')">
                          {{ slotName(m.slot) }}
                          @if (m.shared) {
                            <span class="ui-sub">{{ i18n.t('maint.map.shared') }}</span>
                          } @else if (m.hintCode) {
                            <span class="ui-sub">{{ i18n.t('maint.map.hint', { code: m.hintCode }) }}</span>
                          }
                        </td>
                        <td [attr.data-label]="i18n.t('mfgChoices.map.account')">
                          @if (canAccount() && accounts().length && !m.shared) {
                            <label class="sr-only" [for]="'ms-slot-' + m.slot">{{ slotName(m.slot) }}</label>
                            <select [id]="'ms-slot-' + m.slot" [(ngModel)]="mapDraft[m.slot]" [disabled]="busy()">
                              <option [ngValue]="null">{{ i18n.t('mfgChoices.map.unmapped') }}</option>
                              @for (a of slotAccounts(m.slot); track a.id) {
                                <option [ngValue]="a.id">{{ a.code }} — {{ accountName(a) }}</option>
                              }
                            </select>
                          } @else if (m.ledgerAccountId) {
                            <span class="mono" dir="ltr">{{ m.accountCode }}</span> {{ slotAccountName(m) }}
                          } @else {
                            <span class="ui-sub">{{ i18n.t('mfgChoices.map.unmapped') }}</span>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
              @if (canAccount() && accounts().length) {
                <div class="map-foot bo-field--wide">
                  @if (mapSaved()) {
                    <span class="ui-sub" role="status">{{ i18n.t('maint.map.saved') }}</span>
                  }
                  <button type="button" class="btn-secondary" [disabled]="busy() || !mapDirty()" (click)="saveMap()">{{ i18n.t('mfgChoices.map.save') }}</button>
                </div>
              } @else {
                <p class="ui-sub">{{ i18n.t('maint.accountingReadOnly') }}</p>
              }
            </app-fold>
          </div>
        </section>

        @if (canOperate() || canAccount()) {
          <div class="ui-actionbar ui-actionbar--sticky">
            @if (s.updatedAtUtc) {
              <span class="ui-sub updated">{{ i18n.t('maint.updated', { date: i18n.dateTime(s.updatedAtUtc), who: s.updatedBy ?? '—' }) }}</span>
            }
            <button type="button" class="btn-primary" [disabled]="busy() || !dirty()" (click)="save()">{{ i18n.t('maint.save') }}</button>
          </div>
        } @else {
          <p class="ui-sub">{{ i18n.t('maint.readOnly') }}</p>
        }
      } @else if (error()) {
        <section class="ui-surface"><app-table-state kind="error" [message]="error()" (retry)="load()" /></section>
      }
    </div>

    <ng-template #choice let-c let-editable="editable">
      <div class="bo-field">
        <label [for]="'ms-' + c.field">{{ i18n.t('maint.' + c.field) }}</label>
        <select [id]="'ms-' + c.field" [ngModel]="choiceValue(c)" (ngModelChange)="setChoice(c, $event)" [disabled]="!editable || busy()"
          [attr.aria-describedby]="'ms-' + c.field + '-help'">
          @for (o of c.options; track o; let first = $first) {
            <option [value]="o" [disabled]="isLater(c, o)">
              {{ i18n.t('maint.' + c.field + '.' + o) }}{{ first ? ' ' + i18n.t('maint.suggested') : '' }}{{ isLater(c, o) ? ' ' + i18n.t('maint.later') : '' }}
            </option>
          }
        </select>
        <p [id]="'ms-' + c.field + '-help'" class="bo-hint">{{ c.ms }} · {{ i18n.t('maint.' + c.field + '.help') }}</p>
      </div>
    </ng-template>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .updated {
        margin-inline-end: auto;
        align-self: center;
      }
      .map-foot {
        display: flex;
        justify-content: flex-end;
        align-items: center;
        gap: var(--sp-3);
      }
    `,
  ],
})
export class MaintenanceSettingsComponent implements OnInit {
  private readonly api = inject(MaintenanceApi);
  private readonly ledgerApi = inject(MaintenanceLedgerApi);
  private readonly ledger = inject(LedgerService);
  private readonly receivables = inject(ReceivablesApi);
  private readonly workshopApi = inject(MaintenanceWorkshopApi);
  private readonly auth = inject(AuthService);
  private readonly chrome = inject(PageChrome, { optional: true });
  /** MS-01 as the sidebar reads it: a save that turns the module on or off shows or hides its job pages at once. */
  private readonly module = inject(MaintenanceModule);
  readonly i18n = inject(I18nService);

  readonly settings = signal<MaintenanceSettings | null>(null);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly saved = signal(false);
  /** MS-31: the maintenance slots and the shared stock slots, as saved; the chart to choose from (accounting.view). */
  readonly map = signal<MaintenanceAccountSlot[]>([]);
  readonly accounts = signal<LedgerAccount[]>([]);
  readonly mapSaved = signal(false);
  /** MS-04: the workshop list of the reader's branches, read while the module is on; unreadable, no warning is given. */
  readonly workshop = signal<MaintenanceWorkshop | null>(null);
  /** The branches that employ people but whose workshop list books nobody, by name: what "only the workshop list" leaves unbookable. */
  readonly emptyBranches = computed(() => emptyWorkshopBranches(this.workshop()).map((b) => b.branchName).join(', '));
  /** M7b: customer advances held on 419 (Finance › Settings) — what lets MS-35 take advances. Unreadable: not shown as possible. */
  readonly advancesOn419 = signal(false);
  /** The account map opens while a slot of its own names no account: that is where the accountant's work starts. */
  readonly mapOpen = computed(() => this.canAccount() && this.map().some((m) => !m.shared && !m.ledgerAccountId));
  mapDraft: Record<string, string | null> = {};
  /** MS-01 to MS-27. */
  readonly canOperate = computed(() => mayDo(this.auth.currentUser(), 'maintenance.settings'));
  /** MS-30 to MS-40. */
  readonly canAccount = computed(() => mayDo(this.auth.currentUser(), 'accounting.manage'));

  readonly moduleChoices: readonly Choice[] = [
    { field: 'module', ms: 'MS-01', options: ['Off', 'On'] },
    { field: 'unitsLeaveMaintenance', ms: 'MS-02', options: ['ByHandOrByJob', 'OnlyByJob'] },
    { field: 'jobsFromReturnNotes', ms: 'MS-03', options: ['OfferedInWaitingList', 'DraftJobPerDamagedLine'] },
    // MS-04: "only the workshop list" is taken since the list exists (Maintenance › Workshop).
    { field: 'bookableEmployees', ms: 'MS-04', options: ['AnyEmployeeOfBranch', 'WorkshopListOnly'] },
    { field: 'jobCloser', ms: 'MS-05', options: ['AnyHolder', 'NotTheOpener'] },
    { field: 'customerJobs', ms: 'MS-06', options: ['Off', 'On'] },
  ];
  readonly costChoices: readonly Choice[] = [
    { field: 'costIncludes', ms: 'MS-10', options: ['PartsLabourOutsideWork', 'PartsLabourOutsideWorkOverhead', 'PartsAndOutsideWorkOnly'] },
    { field: 'labourRate', ms: 'MS-12', options: ['PayrollDerived', 'WorkshopRate'] },
    { field: 'burdenAndVat', ms: 'MS-13', options: ['FollowManufacturing', 'OwnValues'] },
  ];
  readonly pricingChoices: readonly Choice[] = [
    { field: 'customerPrice', ms: 'MS-20', options: ['Quoted', 'Itemised', 'CostPlus'] },
    { field: 'returnRecharge', ms: 'MS-23', options: ['TypedAtReturn', 'SuggestedFromRate'] },
    { field: 'priceBelowCost', ms: 'MS-25', options: ['ShownAsLoss', 'NeedsConfirmation'] },
    { field: 'rechargeSharing', ms: 'MS-26', options: ['ByUnits', 'AllToFirstJob'] },
  ];
  readonly accountingChoices: readonly Choice[] = [
    // M7d: capitalised once the REN-12 register holds the hire equipment and its accounts are named (the server checks).
    { field: 'ownRepairs', ms: 'MS-30', options: ['AlwaysExpense', 'CapitalisedAboveAmount'] },
    // M6: "at each issue" is taken once the account map names own equipment repairs' account (the server checks).
    { field: 'partsPosting', ms: 'MS-32', options: ['PracticeLedgerByMonth', 'AtIssue'] },
    { field: 'labourInBooks', ms: 'MS-33', options: ['ReportOnly', 'MovedIntoJobs'] },
    // MS-34: income on its own account is taken once the account map names repair service income (the server checks).
    { field: 'customerIncome', ms: 'MS-34', options: ['SameAsRecharges', 'OneServiceAccount', 'PartsAndLabourApart'] },
    { field: 'advances', ms: 'MS-35', options: ['NotTaken', 'Taken'], later: ['Taken'] },
    { field: 'openJobsAtPeriodEnd', ms: 'MS-36', options: ['LeftInExpensesListed', 'WorkInProgressAtYearEnd', 'WorkInProgressMonthly'] },
    // M7c: the provision is taken once the account map names its two accounts (the server checks), with a rate and a guarantee.
    { field: 'rework', ms: 'MS-37', options: ['AddedToOriginalJob', 'OwnJob', 'Provision'] },
    { field: 'repairHistory', ms: 'MS-38', options: ['PerTypeWithReference', 'PerPiece'] },
  ];

  draft: MaintenanceSettingsDraft = {
    module: 'Off', unitsLeaveMaintenance: 'ByHandOrByJob', jobsFromReturnNotes: 'OfferedInWaitingList',
    bookableEmployees: 'AnyEmployeeOfBranch', jobCloser: 'AnyHolder', customerJobs: 'Off',
    costIncludes: 'PartsLabourOutsideWork', overheadPerLabourHour: 0, labourRate: 'PayrollDerived', workshopRatePerHour: 0,
    burdenAndVat: 'FollowManufacturing', employerBurdenPercent: 0, expenseVatPercent: 0,
    customerPrice: 'Quoted', labourChargePerHour: 0, markupPercent: 0, returnRecharge: 'TypedAtReturn', priceBelowCost: 'ShownAsLoss',
    rechargeSharing: 'ByUnits', guaranteeMonths: 3,
    ownRepairs: 'AlwaysExpense', partsPosting: 'PracticeLedgerByMonth', labourInBooks: 'ReportOnly', customerIncome: 'SameAsRecharges',
    advances: 'NotTaken', openJobsAtPeriodEnd: 'LeftInExpensesListed', rework: 'AddedToOriginalJob', provisionPercent: 0, repairHistory: 'PerTypeWithReference',
    capitaliseAboveAmount: 0, perPieceAboveValue: 0, postingFromMonth: null, accountantApproved: false,
  };

  /** The month box's model: '' stands for no month. */
  get postingFrom(): string {
    return this.draft.postingFromMonth ?? '';
  }
  set postingFrom(value: string | null) {
    this.draft.postingFromMonth = value ? value : null;
  }

  async ngOnInit(): Promise<void> {
    this.chrome?.description.set(this.i18n.t('maint.pageLead'));
    // The chart is read under accounting.view; without it the map is shown as saved, read only.
    this.ledger.listAccounts().then((a) => this.accounts.set(a.filter((x) => x.isActive)), () => undefined);
    this.ledgerApi.accountMap().then((m) => this.takeMap(m), () => undefined);
    // M7b: read under the receipt, finance-settings or accounting keys; without them MS-35 stays "later".
    this.receivables.settings().then((r) => this.advancesOn419.set(r.advancesHeldOn === 'AdvancesAccount'), () => undefined);
    await this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.take(await this.api.settings());
      this.loadWorkshop();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.loading.set(false);
    }
  }

  /** An option kept for a later phase; MS-35's "Taken" is open once customer advances are held on 419 (M7b). */
  isLater(c: Choice, option: string): boolean {
    if (c.field === 'advances' && option === 'Taken' && this.advancesOn419()) return false;
    return !!c.later?.includes(option);
  }

  /** A choice's option name in the draft. */
  choiceValue(c: Choice): string {
    return this.draft[c.field];
  }

  /** Sets a choice's option, by the name its select gave. */
  setChoice(c: Choice, value: string): void {
    (this.draft as unknown as Record<string, unknown>)[c.field] = value;
  }

  dirty(): boolean {
    const s = this.settings();
    if (!s) return false;
    return (Object.keys(this.draft) as (keyof MaintenanceSettingsDraft)[]).some((k) => String(this.draft[k] ?? '') !== String(s[k] ?? ''));
  }

  async save(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.saved.set(false);
    try {
      const d = this.draft;
      const n = (v: unknown) => Number(v) || 0;
      this.take(
        await this.api.saveSettings({
          ...d,
          overheadPerLabourHour: d.costIncludes === 'PartsLabourOutsideWorkOverhead' ? n(d.overheadPerLabourHour) : 0,
          workshopRatePerHour: d.labourRate === 'WorkshopRate' ? n(d.workshopRatePerHour) : 0,
          employerBurdenPercent: d.burdenAndVat === 'OwnValues' ? n(d.employerBurdenPercent) : 0,
          expenseVatPercent: d.burdenAndVat === 'OwnValues' ? n(d.expenseVatPercent) : 0,
          labourChargePerHour: d.customerPrice === 'Itemised' ? n(d.labourChargePerHour) : 0,
          markupPercent: d.customerPrice === 'Quoted' ? 0 : n(d.markupPercent),
          guaranteeMonths: Math.trunc(n(d.guaranteeMonths)),
          provisionPercent: d.rework === 'Provision' ? n(d.provisionPercent) : 0,
          capitaliseAboveAmount: d.ownRepairs === 'CapitalisedAboveAmount' ? n(d.capitaliseAboveAmount) : 0,
          perPieceAboveValue: d.repairHistory === 'PerPiece' ? n(d.perPieceAboveValue) : 0,
        }),
      );
      this.saved.set(true);
      this.loadWorkshop();
    } catch (e) {
      const code = problemCode(e);
      this.error.set(code && REFUSALS[code] ? this.i18n.t(REFUSALS[code]) : adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  /** MS-04's list, read only while the module is on (off, its route answers maintenance_off); a refusal leaves no warning. */
  private loadWorkshop(): void {
    if (this.settings()?.module !== 'On') {
      this.workshop.set(null);
      return;
    }
    this.workshopApi.list().then((w) => this.workshop.set(w), () => this.workshop.set(null));
  }

  mapDirty(): boolean {
    return this.map().some((m) => !m.shared && (this.mapDraft[m.slot] ?? null) !== m.ledgerAccountId);
  }

  async saveMap(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.mapSaved.set(false);
    try {
      const lines = this.map()
        .filter((m) => !m.shared && (this.mapDraft[m.slot] ?? null) !== m.ledgerAccountId)
        .map((m) => ({ slot: m.slot, ledgerAccountId: this.mapDraft[m.slot] ?? null }));
      this.takeMap(await this.ledgerApi.saveAccountMap(lines));
      this.mapSaved.set(true);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  /** The accounts a slot may name: any active one, but repair service income (MS-34) only an income account of class 7, as the server asks. */
  slotAccounts(slot: string): LedgerAccount[] {
    return slot === 'maintenance.service_income'
      ? this.accounts().filter((a) => a.type === 'Income' && a.code.startsWith('7'))
      : this.accounts();
  }

  /** A slot in words: maintenance's own, or a manufacturing stock slot a part may leave. */
  slotName(slot: string): string {
    return this.i18n.t(slot.startsWith('maintenance.') ? 'maintLedger.slot.' + slot : 'mfgChoices.slot.' + slot);
  }

  accountName(a: LedgerAccount): string {
    return localizedName({ nameAr: a.nameAr ?? '', nameFr: a.nameFr ?? '', nameEn: a.nameEn ?? '' }, this.i18n.lang());
  }

  slotAccountName(m: MaintenanceAccountSlot): string {
    return localizedName({ nameAr: m.accountNameAr ?? '', nameFr: m.accountNameFr ?? '', nameEn: m.accountNameEn ?? '' }, this.i18n.lang());
  }

  private takeMap(m: MaintenanceAccountSlot[]): void {
    this.map.set(m);
    this.mapDraft = Object.fromEntries(m.map((x) => [x.slot, x.ledgerAccountId]));
  }

  private take(s: MaintenanceSettings): void {
    this.settings.set(s);
    this.module.set(s.module === 'On');
    const { approvedBy: _ab, approvedAtUtc: _at, updatedAtUtc: _u, updatedBy: _b, assetRegisterActive: _r, ...draft } = s;
    this.draft = { ...draft };
  }
}
