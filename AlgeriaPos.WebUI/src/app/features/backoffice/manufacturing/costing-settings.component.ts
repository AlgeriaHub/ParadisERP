import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { adminErrorMessage } from '../../../core/admin.service';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { mayDo } from '../../../core/landing';
import { LedgerAccount, LedgerService } from '../../../core/ledger.service';
import { localizedName } from '../../../core/multilingual';
import { TableStateComponent } from '../../../ui/table-state.component';
import { MonthInputComponent } from '../payroll/month-input.component';
import { PageChrome } from '../shell/page-chrome';
import { AccountSlot, CostingSettings, CostingSettingsApi, CostingSettingsDraft } from './costing-settings-api.service';

/** One choice on the page: its field, its options (the first is the suggested answer) and the question it answers. */
interface Choice {
  field: keyof Pick<
    CostingSettingsDraft,
    | 'model' | 'releaseRule' | 'employerBurdenUse' | 'monthEndWorkInProgress' | 'lateCosts' | 'variances' | 'expenseBasis' | 'scrapRecovery'
    | 'labourAbsorption'
  >;
  question: string;
  options: readonly string[];
}

/**
 * Manufacturing › Costing settings (Phase C1, completed 2026-11-09). Every costing and accounting question of the plan is
 * answered here, in the panel, by the accountant (owner: "make everything as settings and accountant set it in cpanel"):
 * the choices, the stock-operations switch (C-12) and the accountant's confirmation, which lets a cost sheet with final
 * inputs read approved. The first option of each is the suggested answer and the default. The cost previews follow at once;
 * a frozen cost sheet keeps the choices it was frozen under; nothing here posts to the ledger. The account map says
 * which account each role and event will book to, seeded empty; an empty slot stays unmapped and is never guessed.
 */
@Component({
  selector: 'app-mfg-costing-settings',
  standalone: true,
  imports: [FormsModule, MonthInputComponent, TableStateComponent],
  template: `
    <div class="bo-stack">
      <p class="notice info">{{ i18n.t('mfgChoices.note') }}</p>
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (saved()) {
        <p class="notice ok" role="status">{{ i18n.t('mfgChoices.saved') }}</p>
      }

      @if (loading() && !settings()) {
        <section class="ui-surface"><app-table-state kind="loading" /></section>
      } @else if (settings(); as s) {
        <section class="ui-surface" aria-labelledby="mc-choices-title">
          <header class="bo-panel__head">
            <h2 id="mc-choices-title" class="bo-panel__title">{{ i18n.t('mfgChoices.title') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('mfgChoices.lead') }}</p>
          </header>
          <div class="body">
            <div class="grid">
              @for (c of choices; track c.field) {
                <div class="field">
                  <label [for]="'mc-' + c.field">{{ i18n.t('mfgChoices.' + c.field) }}</label>
                  <select [id]="'mc-' + c.field" [(ngModel)]="draft[c.field]" [disabled]="!canEdit() || busy()"
                    [attr.aria-describedby]="'mc-' + c.field + '-help'">
                    @for (o of c.options; track o; let first = $first) {
                      <option [value]="o">{{ i18n.t('mfgChoices.' + c.field + '.' + o) }}{{ first ? ' ' + i18n.t('mfgChoices.suggested') : '' }}</option>
                    }
                  </select>
                  <small [id]="'mc-' + c.field + '-help'" class="help">{{ c.question }} · {{ i18n.t('mfgChoices.' + c.field + '.help') }}</small>
                </div>
              }
              <div class="field">
                <label for="mc-overhead">{{ i18n.t('mfgChoices.overhead') }}</label>
                <input id="mc-overhead" type="number" min="0" step="0.001" class="mono" [(ngModel)]="draft.overheadPerLabourHour"
                  [disabled]="!canEdit() || busy()" aria-describedby="mc-overhead-help" />
                <small id="mc-overhead-help" class="help">C-08 · {{ i18n.t('mfgChoices.overhead.help') }}</small>
              </div>
              @if (draft.employerBurdenUse !== 'None') {
                <div class="field">
                  <label for="mc-burden">{{ i18n.t('mfgChoices.burdenPercent') }}</label>
                  <input id="mc-burden" type="number" min="0" max="100" step="0.01" class="mono" [(ngModel)]="draft.employerBurdenPercent"
                    [disabled]="!canEdit() || busy()" aria-describedby="mc-burden-help" />
                  <small id="mc-burden-help" class="help">C-06 · {{ i18n.t('mfgChoices.burdenPercent.help') }}</small>
                </div>
              }
              @if (draft.expenseBasis === 'BeforeRecoverableVat') {
                <div class="field">
                  <label for="mc-vat">{{ i18n.t('mfgChoices.vatPercent') }}</label>
                  <input id="mc-vat" type="number" min="0" max="100" step="0.01" class="mono" [(ngModel)]="draft.expenseVatPercent"
                    [disabled]="!canEdit() || busy()" aria-describedby="mc-vat-help" />
                  <small id="mc-vat-help" class="help">C-07 · {{ i18n.t('mfgChoices.vatPercent.help') }}</small>
                </div>
              }
              @if (draft.scrapRecovery !== 'None') {
                <div class="field">
                  <label for="mc-scrap">{{ i18n.t('mfgChoices.scrapPercent') }}</label>
                  <input id="mc-scrap" type="number" min="0" max="100" step="0.01" class="mono" [(ngModel)]="draft.scrapRecoveryPercent"
                    [disabled]="!canEdit() || busy()" aria-describedby="mc-scrap-help" />
                  <small id="mc-scrap-help" class="help">C-09 · {{ i18n.t('mfgChoices.scrapPercent.help') }}</small>
                </div>
              }
            </div>

            <!-- C-12: the accountant opens manufacturing's stock operations here (owner, 2026-11-09). -->
            <div class="field">
              <label class="check">
                <input id="mc-stock-operations" type="checkbox" [(ngModel)]="draft.stockOperations" [disabled]="!canEdit() || busy()"
                  aria-describedby="mc-stock-operations-help" />
                {{ i18n.t('mfgChoices.stockOperations') }}
              </label>
              <small id="mc-stock-operations-help" class="help">C-12 · {{ i18n.t('mfgChoices.stockOperations.help') }}</small>
              @if (s.stockOperationsByServer) {
                <small class="help">{{ i18n.t('mfgChoices.stockOperations.byServer') }}</small>
              }
            </div>

            <!-- Phase D: from which month production may post to the ledger; empty, nothing posts. -->
            <div class="field">
              <label for="mc-posting-from">{{ i18n.t('mfgChoices.postingFrom') }}</label>
              <app-month-input id="mc-posting-from" [(ngModel)]="postingFrom" [disabled]="!canEdit() || busy()" />
              <small class="help">{{ i18n.t('mfgChoices.postingFrom.help') }}</small>
            </div>

            <!-- The accountant's confirmation of the choices above, given here instead of on paper. -->
            <div class="field approval">
              <label class="check">
                <input id="mc-approved" type="checkbox" [(ngModel)]="draft.accountantApproved" [disabled]="!canEdit() || busy()"
                  aria-describedby="mc-approved-help" />
                {{ i18n.t('mfgChoices.approval.confirm') }}
              </label>
              <small id="mc-approved-help" class="help">{{ i18n.t('mfgChoices.approval.help') }}</small>
              @if (s.accountantApproved && s.approvedAtUtc) {
                <span class="ui-sub">{{ i18n.t('mfgChoices.approval.by', { who: s.approvedBy ?? '—', date: i18n.dateTime(s.approvedAtUtc) }) }}</span>
              } @else {
                <span class="ui-sub">{{ i18n.t('mfgChoices.approval.not') }}</span>
              }
            </div>
            @if (canEdit()) {
              <div class="foot">
                @if (s.updatedAtUtc) {
                  <span class="ui-sub">{{ i18n.t('mfgChoices.updated', { date: i18n.dateTime(s.updatedAtUtc), who: s.updatedBy ?? '—' }) }}</span>
                }
                <button type="button" class="btn-primary" [disabled]="busy() || !dirty()" (click)="save()">{{ i18n.t('mfgChoices.save') }}</button>
              </div>
            } @else {
              <p class="ui-sub">{{ i18n.t('mfgChoices.readOnly') }}</p>
            }
          </div>
        </section>

        <section class="ui-surface" aria-labelledby="mc-map-title">
          <header class="bo-panel__head">
            <h2 id="mc-map-title" class="bo-panel__title">{{ i18n.t('mfgChoices.map.title') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('mfgChoices.map.lead') }}</p>
          </header>
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('mfgChoices.map.slot') }}</th>
                  <th>{{ i18n.t('mfgChoices.map.account') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (m of map(); track m.slot) {
                  <tr>
                    <td [attr.data-label]="i18n.t('mfgChoices.map.slot')">{{ i18n.t('mfgChoices.slot.' + m.slot) }}</td>
                    <td [attr.data-label]="i18n.t('mfgChoices.map.account')">
                      @if (canEdit() && accounts().length) {
                        <label class="sr-only" [for]="'mc-slot-' + m.slot">{{ i18n.t('mfgChoices.slot.' + m.slot) }}</label>
                        <select [id]="'mc-slot-' + m.slot" [(ngModel)]="mapDraft[m.slot]" [disabled]="busy()">
                          <option [ngValue]="null">{{ i18n.t('mfgChoices.map.unmapped') }}</option>
                          @for (a of accounts(); track a.id) {
                            <option [ngValue]="a.id">{{ a.code }} — {{ name(a) }}</option>
                          }
                        </select>
                      } @else if (m.ledgerAccountId) {
                        <span class="mono" dir="ltr">{{ m.accountCode }}</span> {{ accountName(m) }}
                      } @else {
                        <span class="ui-sub">{{ i18n.t('mfgChoices.map.unmapped') }}</span>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          @if (canEdit() && accounts().length) {
            <div class="foot pad">
              <button type="button" class="btn-primary" [disabled]="busy() || !mapDirty()" (click)="saveMap()">{{ i18n.t('mfgChoices.map.save') }}</button>
            </div>
          }
        </section>
      } @else if (error()) {
        <section class="ui-surface"><app-table-state kind="error" [message]="error()" (retry)="load()" /></section>
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .body {
        padding: var(--sp-4);
        display: grid;
        gap: var(--sp-4);
      }
      .grid {
        display: grid;
        gap: var(--sp-4);
        grid-template-columns: repeat(auto-fit, minmax(16rem, 1fr));
      }
      .field {
        display: grid;
        gap: var(--sp-1);
        align-content: start;
      }
      .help {
        color: var(--muted);
      }
      .check {
        display: flex;
        align-items: center;
        gap: var(--sp-2);
        font-weight: 600;
      }
      .approval {
        padding-block-start: var(--sp-3);
        border-block-start: var(--line, 1px) solid var(--border);
      }
      .foot {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: flex-end;
        gap: var(--sp-3);
      }
      .foot.pad {
        padding: var(--sp-3) var(--sp-4);
      }
    `,
  ],
})
export class MfgCostingSettingsComponent implements OnInit {
  private readonly api = inject(CostingSettingsApi);
  private readonly ledger = inject(LedgerService);
  private readonly auth = inject(AuthService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  readonly settings = signal<CostingSettings | null>(null);
  readonly map = signal<AccountSlot[]>([]);
  readonly accounts = signal<LedgerAccount[]>([]);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly saved = signal(false);
  readonly canEdit = computed(() => mayDo(this.auth.currentUser(), 'accounting.manage'));

  readonly choices: readonly Choice[] = [
    { field: 'model', question: 'C-03', options: ['ByNature', 'WorkInProgress'] },
    { field: 'releaseRule', question: 'C-11', options: ['StandardMaterialPlusConversion', 'EstimatedUnitCost', 'CostToDate'] },
    { field: 'employerBurdenUse', question: 'C-06', options: ['None', 'ReportsOnly', 'StockValue'] },
    { field: 'monthEndWorkInProgress', question: 'C-15', options: ['Monthly', 'YearEndOnly'] },
    { field: 'lateCosts', question: 'C-10', options: ['StayInCharges', 'VarianceAccount'] },
    { field: 'variances', question: 'C-13', options: ['ManagementOnly', 'VarianceAccount'] },
    { field: 'expenseBasis', question: 'C-07', options: ['BeforeRecoverableVat', 'AsRecorded'] },
    { field: 'scrapRecovery', question: 'C-09', options: ['None', 'PercentOfMaterial'] },
    // C-05 (2026-11-09): bookings now mark their overtime hours, so the premium can be kept to them.
    { field: 'labourAbsorption', question: 'C-05', options: ['BlendedActualRate', 'PremiumToOvertime'] },
  ];

  draft: CostingSettingsDraft = {
    model: 'ByNature', overheadPerLabourHour: 0, releaseRule: 'StandardMaterialPlusConversion', employerBurdenUse: 'None',
    employerBurdenPercent: 0, monthEndWorkInProgress: 'Monthly', lateCosts: 'StayInCharges', variances: 'ManagementOnly',
    expenseBasis: 'BeforeRecoverableVat', expenseVatPercent: 0, scrapRecovery: 'None', scrapRecoveryPercent: 0,
    stockOperations: false, accountantApproved: false, postingFromMonth: null, labourAbsorption: 'BlendedActualRate',
  };
  /** The month box's model: '' stands for no month. */
  get postingFrom(): string {
    return this.draft.postingFromMonth ?? '';
  }
  set postingFrom(value: string | null) {
    this.draft.postingFromMonth = value ? value : null;
  }
  mapDraft: Record<string, string | null> = {};

  async ngOnInit(): Promise<void> {
    this.chrome?.description.set(this.i18n.t('mfgChoices.pageLead'));
    // The chart is read under accounting.view; without it the map is shown as saved, read only.
    this.ledger.listAccounts().then((a) => this.accounts.set(a.filter((x) => x.isActive)), () => undefined);
    await this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const [s, m] = await Promise.all([this.api.settings(), this.api.accountMap()]);
      this.take(s);
      this.takeMap(m);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.loading.set(false);
    }
  }

  dirty(): boolean {
    const s = this.settings();
    if (!s) return false;
    return (Object.keys(this.draft) as (keyof CostingSettingsDraft)[]).some((k) => String(this.draft[k]) !== String(s[k]));
  }

  mapDirty(): boolean {
    return this.map().some((m) => (this.mapDraft[m.slot] ?? null) !== m.ledgerAccountId);
  }

  async save(): Promise<void> {
    await this.run(async () => {
      const draft = { ...this.draft, overheadPerLabourHour: Number(this.draft.overheadPerLabourHour) || 0,
        employerBurdenPercent: this.draft.employerBurdenUse === 'None' ? 0 : Number(this.draft.employerBurdenPercent) || 0,
        expenseVatPercent: this.draft.expenseBasis === 'AsRecorded' ? 0 : Number(this.draft.expenseVatPercent) || 0,
        scrapRecoveryPercent: this.draft.scrapRecovery === 'None' ? 0 : Number(this.draft.scrapRecoveryPercent) || 0 };
      this.take(await this.api.saveSettings(draft));
    });
  }

  async saveMap(): Promise<void> {
    await this.run(async () => {
      const lines = this.map()
        .filter((m) => (this.mapDraft[m.slot] ?? null) !== m.ledgerAccountId)
        .map((m) => ({ slot: m.slot, ledgerAccountId: this.mapDraft[m.slot] ?? null }));
      this.takeMap(await this.api.saveAccountMap(lines));
    });
  }

  name(a: LedgerAccount): string {
    return localizedName(a, this.i18n.lang());
  }

  accountName(m: AccountSlot): string {
    return localizedName({ nameAr: m.accountNameAr ?? '', nameFr: m.accountNameFr ?? '', nameEn: m.accountNameEn ?? '' }, this.i18n.lang());
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.saved.set(false);
    try {
      await action();
      this.saved.set(true);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  private take(s: CostingSettings): void {
    this.settings.set(s);
    const { updatedAtUtc: _u, updatedBy: _b, stockOperationsByServer: _s, approvedBy: _ab, approvedAtUtc: _at, ...draft } = s;
    this.draft = { ...draft };
  }

  private takeMap(m: AccountSlot[]): void {
    this.map.set(m);
    this.mapDraft = Object.fromEntries(m.map((x) => [x.slot, x.ledgerAccountId]));
  }
}
