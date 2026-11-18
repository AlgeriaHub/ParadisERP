import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { problemCode } from '../../../core/admin.service';
import { adminErrorMessage } from '../../../core/error-message';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { mayDo } from '../../../core/landing';
import { LedgerAccount, LedgerService } from '../../../core/ledger.service';
import { localizedName } from '../../../core/multilingual';
import { formatNumber } from '../../../core/number-format';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest } from '../../../ui/confirm-requests';
import { TableStateComponent } from '../../../ui/table-state.component';
import { MonthInputComponent } from '../payroll/month-input.component';
import {
  DepreciationMethod,
  FixedAssetLife,
  FixedAssetMethodLine,
  FixedAssetOpeningValue,
  FixedAssetSettings,
  FixedAssetSlot,
  FixedAssetSwitch,
  FixedAssetsApi,
  RentalEquipmentTreatment,
} from './fixed-assets-api.service';

/** The settings' refusals in the reader's language; the switch-over's blocking reasons are keyed by their code too. */
const REFUSALS: Readonly<Record<string, string>> = {
  method_locked: 'fa.error.methodLocked',
  depreciation_locked: 'fa.error.locked',
  life_locked: 'fa.error.lifeLocked',
  slot_in_use: 'fa.error.slotInUse',
  switch_over_changed: 'fa.error.changed',
  fixed_assets_company_wide: 'fa.error.companyWide',
  books_closed: 'maintLedger.error.booksClosed',
};

/** A type's own method as the table edits it: '' follows the company's. */
export interface MethodDraft {
  method: DepreciationMethod | '';
  coefficient: number | null;
  switch: '' | 'on' | 'off';
}

/** The line the server reads from a drafted row: each empty field null (the company's). */
export function methodLine(productId: string, d: MethodDraft | undefined): FixedAssetMethodLine {
  const coefficient = d?.coefficient === null || d?.coefficient === undefined || (d.coefficient as unknown) === '' ? null : Number(d.coefficient);
  return {
    productId,
    method: d?.method || null,
    decliningCoefficient: coefficient === null || Number.isNaN(coefficient) ? null : coefficient,
    switchToStraightLine: d?.switch === 'on' ? true : d?.switch === 'off' ? false : null,
  };
}

/**
 * The tax code's declining coefficient for a life in months, as the server suggests it (`FixedAssetDeclining.LegalCoefficient`):
 * 1.5 up to four years, 2 for five and six, 2.5 above six. None without a life.
 */
export function legalCoefficient(lifeMonths: number | null): number | null {
  if (!lifeMonths || lifeMonths <= 0) return null;
  return lifeMonths > 72 ? 2.5 : lifeMonths >= 60 ? 2 : 1.5;
}

const BLOCKING = new Set(['treatment_stock', 'month_not_chosen', 'month_not_begun', 'month_passed', 'slot_unmapped', 'life_missing', 'books_closed', 'already_posted']);

/**
 * Reports › Fixed assets › Settings (REN-12, Phase M7a): every choice the accountant makes for hire equipment, the suggested
 * answer first and today's behaviour kept until the switch-over is posted — the treatment (stock, as today, or fixed assets),
 * the method (straight line, or declining balance with its coefficient and its switch to straight line — the company's default
 * and each type's own, fixed on a type once depreciation is posted on it), the useful life per type and for all, the residual
 * value, the value the pools open at, the switch-over month; the account map's slots, seeded empty and never guessed; and the
 * switch-over itself, previewed and posted once. Read with the accounting reports' key; changed under `accounting.manage`.
 */
@Component({
  selector: 'app-fixed-asset-settings',
  standalone: true,
  imports: [FormsModule, MonthInputComponent, TableStateComponent],
  template: `
    @if (error()) {
      <p class="notice danger" role="alert">{{ error() }}</p>
    }
    @if (!settings() && busy()) {
      <app-table-state kind="loading" />
    }
    @if (settings(); as s) {
      @if (s.active) {
        <p class="notice info" role="status">
          {{ i18n.t('fa.settings.switched', { who: s.switchedBy ?? '—', date: s.switchedAtUtc ? i18n.dateTime(s.switchedAtUtc) : '—' }) }}
        </p>
      }
      <section class="ui-surface fa-card" [attr.aria-label]="i18n.t('fa.settings.title')">
        <h2 class="bo-panel__title">{{ i18n.t('fa.settings.title') }}</h2>
        <p class="bo-hint">REN-12 · {{ i18n.t('fa.settings.lead') }}</p>
        <div class="bo-form bo-form--full">
          <div class="bo-field">
            <label for="fa-treatment">{{ i18n.t('fa.settings.treatment') }}</label>
            <select id="fa-treatment" [(ngModel)]="draft.treatment" [disabled]="!canManage() || s.depreciationLocked || busy()">
              <option value="Stock">{{ i18n.t('fa.treatment.Stock') }}</option>
              <option value="FixedAssets">{{ i18n.t('fa.treatment.FixedAssets') }}</option>
            </select>
          </div>
          <div class="bo-field">
            <label for="fa-method">{{ i18n.t('fa.settings.method') }}</label>
            <select id="fa-method" [(ngModel)]="draft.method" [disabled]="!canManage() || !!s.methodLocked || busy()">
              <option value="StraightLine">{{ i18n.t('fa.method.StraightLine') }}</option>
              <option value="DecliningBalance">{{ i18n.t('fa.method.DecliningBalance') }}</option>
            </select>
            <span class="ui-sub">{{ i18n.t('fa.settings.methodHint') }}</span>
            @if (s.methodLocked) {
              <span class="ui-sub">{{ i18n.t('fa.settings.defaultMethodFixed') }}</span>
            }
          </div>
          <div class="bo-field">
            <label class="bo-check">
              <input id="fa-switch-sl" type="checkbox" aria-describedby="fa-switch-hint" [(ngModel)]="draft.switchToStraightLine"
                [disabled]="!canManage() || !!s.methodLocked || busy()" />
              {{ i18n.t('fa.settings.switchToStraightLine') }}
            </label>
            <span class="ui-sub" id="fa-switch-hint">{{ i18n.t('fa.settings.switchHint') }}</span>
          </div>
          <div class="bo-field">
            <label for="fa-life">{{ i18n.t('fa.settings.defaultLife') }}</label>
            <input id="fa-life" type="number" min="1" max="600" step="1" [(ngModel)]="draft.defaultUsefulLifeMonths"
              [disabled]="!canManage() || s.depreciationLocked || busy()" />
            <span class="ui-sub">{{ i18n.t('fa.settings.lifeHint') }}</span>
          </div>
          <div class="bo-field">
            <label for="fa-residual">{{ i18n.t('fa.settings.residual') }}</label>
            <input id="fa-residual" type="number" min="0" max="99" step="0.5" [(ngModel)]="draft.residualPercent"
              [disabled]="!canManage() || s.depreciationLocked || busy()" />
          </div>
          <div class="bo-field">
            <label for="fa-opening">{{ i18n.t('fa.settings.openingValue') }}</label>
            <select id="fa-opening" [(ngModel)]="draft.openingValue" [disabled]="!canManage() || s.depreciationLocked || busy()">
              <option value="CostInForce">{{ i18n.t('fa.opening.CostInForce') }}</option>
              <option value="AssetUnitValue">{{ i18n.t('fa.opening.AssetUnitValue') }}</option>
            </select>
          </div>
          <div class="bo-field">
            <label for="fa-switch-month">{{ i18n.t('fa.settings.switchMonth') }}</label>
            <app-month-input id="fa-switch-month" [(ngModel)]="draft.switchMonth" [disabled]="!canManage() || s.depreciationLocked || busy()" />
            <span class="ui-sub">{{ i18n.t('fa.settings.switchMonthHint') }}</span>
          </div>
        </div>

        @if (s.lives.length) {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <caption>{{ i18n.t('fa.settings.lives') }}</caption>
              <thead>
                <tr>
                  <th>{{ i18n.t('fa.equipment') }}</th>
                  <th>{{ i18n.t('fa.settings.ownLife') }}</th>
                  <th>{{ i18n.t('fa.settings.ownMethod') }}</th>
                  <th>{{ i18n.t('fa.settings.coefficient') }}</th>
                  <th>{{ i18n.t('fa.settings.ownSwitch') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (l of s.lives; track l.productId) {
                  <tr>
                    <td [attr.data-label]="i18n.t('fa.equipment')">{{ lifeName(l) }} <span class="ui-sub mono" dir="ltr">{{ l.sku }}</span></td>
                    <td [attr.data-label]="i18n.t('fa.settings.ownLife')">
                      <input type="number" min="1" max="600" step="1" [attr.aria-label]="i18n.t('fa.settings.ownLife') + ' ' + l.sku"
                        [(ngModel)]="lifeDraft[l.productId]" [disabled]="!canManage() || l.locked || busy()"
                        [placeholder]="i18n.t('fa.settings.useDefault')" />
                      @if (l.locked) {
                        <span class="ui-sub">{{ i18n.t('fa.settings.lifeFixed') }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('fa.settings.ownMethod')">
                      <select [attr.aria-label]="i18n.t('fa.settings.ownMethod') + ' ' + l.sku" [(ngModel)]="methodDraft[l.productId].method"
                        [disabled]="!canManage() || !!l.methodLocked || busy()">
                        <option value="">{{ i18n.t('fa.settings.followCompany', { value: i18n.t('fa.method.' + draft.method) }) }}</option>
                        <option value="StraightLine">{{ i18n.t('fa.method.StraightLine') }}</option>
                        <option value="DecliningBalance">{{ i18n.t('fa.method.DecliningBalance') }}</option>
                      </select>
                      @if (l.methodLocked) {
                        <span class="ui-sub">{{ i18n.t('fa.settings.methodFixed') }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('fa.settings.coefficient')">
                      @if (declines(l)) {
                        <input type="number" min="1" max="5" step="0.5" [attr.aria-label]="i18n.t('fa.settings.coefficient') + ' ' + l.sku"
                          [(ngModel)]="methodDraft[l.productId].coefficient" [disabled]="!canManage() || !!l.methodLocked || busy()"
                          [placeholder]="suggested(l)" />
                        <span class="ui-sub">{{ i18n.t('fa.settings.coefficientSuggested', { value: suggested(l) }) }}</span>
                      } @else {
                        <span class="ui-sub">{{ i18n.t('fa.settings.straightLineOnly') }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('fa.settings.ownSwitch')">
                      @if (declines(l)) {
                        <select [attr.aria-label]="i18n.t('fa.settings.ownSwitch') + ' ' + l.sku" [(ngModel)]="methodDraft[l.productId].switch"
                          [disabled]="!canManage() || !!l.methodLocked || busy()">
                          <option value="">{{ i18n.t('fa.settings.followCompany', { value: i18n.t(draft.switchToStraightLine ? 'fa.settings.yes' : 'fa.settings.no') }) }}</option>
                          <option value="on">{{ i18n.t('fa.settings.yes') }}</option>
                          <option value="off">{{ i18n.t('fa.settings.no') }}</option>
                        </select>
                      } @else {
                        <span class="ui-sub">—</span>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
        @if (canManage()) {
          <div class="fa-foot">
            <button type="button" class="btn-primary" data-testid="fa-save" [disabled]="busy()" (click)="save()">{{ i18n.t('fa.settings.save') }}</button>
          </div>
        } @else {
          <p class="ui-sub">{{ i18n.t('maint.accountingReadOnly') }}</p>
        }
      </section>

      <section class="ui-surface fa-card" [attr.aria-label]="i18n.t('fa.map.title')">
        <h2 class="bo-panel__title">{{ i18n.t('fa.map.title') }}</h2>
        <p class="bo-hint">{{ i18n.t('fa.map.lead') }}</p>
        <div class="ui-scroll">
          <table class="ui-table ui-table--cards">
            <caption class="sr-only">{{ i18n.t('fa.map.title') }}</caption>
            <thead>
              <tr>
                <th>{{ i18n.t('mfgChoices.map.slot') }}</th>
                <th>{{ i18n.t('mfgChoices.map.account') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (m of s.slots; track m.slot) {
                <tr>
                  <td [attr.data-label]="i18n.t('mfgChoices.map.slot')">
                    <label [for]="'fa-slot-' + m.slot">{{ i18n.t('fa.slot.' + m.slot) }}</label>
                    <span class="ui-sub">
                      {{ i18n.t(m.required ? 'fa.map.required' : 'fa.map.whenNeeded') }}
                      @if (m.hintCode) { · {{ i18n.t('maint.map.hint', { code: m.hintCode }) }} }
                    </span>
                  </td>
                  <td [attr.data-label]="i18n.t('mfgChoices.map.account')">
                    @if (canManage() && accounts().length) {
                      <select [id]="'fa-slot-' + m.slot" [(ngModel)]="mapDraft[m.slot]" [disabled]="busy()">
                        <option [ngValue]="null">{{ i18n.t('mfgChoices.map.unmapped') }}</option>
                        @for (a of accounts(); track a.id) {
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
        @if (canManage() && accounts().length) {
          <div class="fa-foot">
            <button type="button" class="btn-secondary" [disabled]="busy() || !mapDirty()" (click)="saveMap()">{{ i18n.t('mfgChoices.map.save') }}</button>
          </div>
        }
      </section>

      @if (canManage()) {
        <section class="ui-surface fa-card" [attr.aria-label]="i18n.t('fa.switch.title')" data-testid="fa-switch">
          <h2 class="bo-panel__title">{{ i18n.t('fa.switch.title') }}</h2>
          <p class="bo-hint">{{ i18n.t('fa.switch.lead') }}</p>
          @if (preview(); as p) {
            @if (p.posted) {
              <p class="notice info">{{ i18n.t('fa.switch.posted', { who: p.switchedBy ?? '—', date: p.switchedAtUtc ? i18n.dateTime(p.switchedAtUtc) : '—' }) }}</p>
            } @else if (p.blocking.length) {
              <ul class="notice warn fa-blocking" role="status">
                @for (b of p.blocking; track $index) {
                  <li>{{ blockingText(b.code, b.message) }}</li>
                }
              </ul>
            }
            <div class="ui-metrics">
              <div class="ui-metric ui-metric--lead">
                <span class="ui-metric__label">{{ i18n.t('fa.switch.stockValue') }}</span>
                <strong class="ui-metric__value mono" data-testid="fa-switch-value">{{ i18n.money(p.stockValue) }}</strong>
              </div>
              <div class="ui-metric">
                <span class="ui-metric__label">{{ i18n.t('fa.switch.openingTotal') }}</span>
                <strong class="ui-metric__value mono">{{ i18n.money(p.openingTotal) }}</strong>
              </div>
              <div class="ui-metric">
                <span class="ui-metric__label">{{ i18n.t('fa.switch.difference') }}</span>
                <strong class="ui-metric__value mono">{{ i18n.money(p.difference) }}</strong>
              </div>
            </div>
            <p class="ui-sub">{{ i18n.t('fa.switch.costMethod.' + p.costMethod) }}</p>
            @if (p.lines.length) {
              <div class="ui-scroll">
                <table class="ui-table ui-table--cards">
                  <caption class="sr-only">{{ i18n.t('fa.switch.title') }}</caption>
                  <thead>
                    <tr>
                      <th>{{ i18n.t('fa.equipment') }}</th>
                      <th>{{ i18n.t('missingCost.branch') }}</th>
                      <th class="num">{{ i18n.t('fa.units') }}</th>
                      <th class="num">{{ i18n.t('fa.switch.unitCost') }}</th>
                      <th class="num">{{ i18n.t('fa.switch.stockValue') }}</th>
                      <th class="num">{{ i18n.t('fa.switch.openingTotal') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (l of p.lines; track l.productId + l.branchId) {
                      <tr>
                        <td [attr.data-label]="i18n.t('fa.equipment')">{{ lineName(l) }} <span class="ui-sub mono" dir="ltr">{{ l.sku }}</span></td>
                        <td [attr.data-label]="i18n.t('missingCost.branch')">{{ lineBranch(l) }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('fa.units')">
                          {{ l.qty }}
                          @if (l.onRent || l.inMaintenance || l.inTransit) {
                            <span class="ui-sub">{{ i18n.t('fa.switch.split', { onHand: l.onHand, onRent: l.onRent, maintenance: l.inMaintenance, transit: l.inTransit }) }}</span>
                          }
                        </td>
                        <td class="num mono" [attr.data-label]="i18n.t('fa.switch.unitCost')">{{ i18n.money(l.unitCost) }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('fa.switch.stockValue')">{{ i18n.money(l.stockValue) }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('fa.switch.openingTotal')">{{ i18n.money(l.openingValue) }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
            @if (!p.posted) {
              <div class="fa-foot">
                <button type="button" class="btn-ghost" [disabled]="busy()" (click)="loadPreview()">{{ i18n.t('subscription.refresh') }}</button>
                <button type="button" class="btn-primary" data-testid="fa-switch-post" [disabled]="busy() || p.blocking.length > 0" (click)="switchOver()">
                  {{ i18n.t('fa.switch.post') }}
                </button>
              </div>
            }
          } @else {
            <app-table-state kind="loading" />
          }
        </section>
      }
    }
  `,
  styles: [
    `
      .fa-card {
        padding: var(--sp-4);
        display: grid;
        gap: var(--sp-3);
      }
      .fa-foot {
        display: flex;
        justify-content: flex-end;
        gap: var(--sp-2);
        flex-wrap: wrap;
      }
      .fa-blocking {
        margin: 0;
        padding-inline-start: var(--sp-5);
      }
    `,
  ],
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
})
export class FixedAssetSettingsComponent implements OnInit {
  private readonly api = inject(FixedAssetsApi);
  private readonly ledger = inject(LedgerService);
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmService);
  readonly i18n = inject(I18nService);

  readonly settings = signal<FixedAssetSettings | null>(null);
  readonly preview = signal<FixedAssetSwitch | null>(null);
  readonly accounts = signal<LedgerAccount[]>([]);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  /** The settings, the account map and the switch-over are the accountant's: accounting.manage, checked again by the server. */
  readonly canManage = computed(() => mayDo(this.auth.currentUser(), 'accounting.manage'));

  draft: {
    treatment: RentalEquipmentTreatment;
    method: DepreciationMethod;
    defaultUsefulLifeMonths: number | null;
    residualPercent: number;
    openingValue: FixedAssetOpeningValue;
    switchMonth: string;
    switchToStraightLine: boolean;
  } = {
    treatment: 'Stock', method: 'StraightLine', defaultUsefulLifeMonths: null, residualPercent: 0, openingValue: 'CostInForce', switchMonth: '',
    switchToStraightLine: true,
  };
  lifeDraft: Record<string, number | null> = {};
  /** Each type's own method: '' (and a null coefficient) follows the company's. */
  methodDraft: Record<string, MethodDraft> = {};
  mapDraft: Record<string, string | null> = {};

  async ngOnInit(): Promise<void> {
    this.busy.set(true);
    try {
      this.take(await this.api.settings());
    } catch (e) {
      this.error.set(this.message(e));
    } finally {
      this.busy.set(false);
    }
    // The chart is read under accounting.view; without it the map is shown as saved, read only.
    this.ledger.listAccounts().then((a) => this.accounts.set(a.filter((x) => x.isActive)), () => undefined);
    if (this.canManage()) await this.loadPreview();
  }

  mapDirty(): boolean {
    return (this.settings()?.slots ?? []).some((m) => (this.mapDraft[m.slot] ?? null) !== m.ledgerAccountId);
  }

  async loadPreview(): Promise<void> {
    try {
      this.preview.set(await this.api.switchOver());
    } catch (e) {
      this.error.set(this.message(e));
    }
  }

  async save(): Promise<void> {
    const s = this.settings();
    if (!s) return;
    const lives = s.lives
      .filter((l) => !l.locked && (this.lifeDraft[l.productId] ?? null) !== l.usefulLifeMonths)
      .map((l) => ({ productId: l.productId, usefulLifeMonths: this.lifeDraft[l.productId] || null }));
    // Only the types whose own method changed; the server refuses a change on a type with depreciation posted on it.
    const methods = s.lives
      .map((l) => methodLine(l.productId, this.methodDraft[l.productId]))
      .filter((m, i) => {
        const l = s.lives[i];
        return !l.methodLocked && (m.method !== (l.method ?? null) || m.decliningCoefficient !== (l.decliningCoefficient ?? null)
          || m.switchToStraightLine !== (l.switchToStraightLine ?? null));
      });
    await this.run(async () => {
      this.take(
        await this.api.saveSettings({
          treatment: this.draft.treatment,
          method: this.draft.method,
          defaultUsefulLifeMonths: this.draft.defaultUsefulLifeMonths || null,
          residualPercent: Number(this.draft.residualPercent) || 0,
          openingValue: this.draft.openingValue,
          switchMonth: this.draft.switchMonth || null,
          lives,
          switchToStraightLine: this.draft.switchToStraightLine,
          methods,
        }),
      );
      if (this.canManage()) await this.loadPreview();
    });
  }

  async saveMap(): Promise<void> {
    const s = this.settings();
    if (!s) return;
    const lines = s.slots.map((m) => ({ slot: m.slot, ledgerAccountId: this.mapDraft[m.slot] ?? null }));
    await this.run(async () => {
      this.take(await this.api.saveAccountMap(lines));
      await this.loadPreview();
    });
  }

  async switchOver(): Promise<void> {
    const p = this.preview();
    if (!p || !(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('fa.switch.post'))))) return;
    await this.run(async () => {
      this.preview.set(await this.api.postSwitchOver(p.stockValue, p.openingTotal));
      this.take(await this.api.settings());
    });
  }

  private async run(work: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await work();
    } catch (e) {
      this.error.set(this.message(e));
      // A figure that moved since it was shown: read it again so the next confirmation states the new one.
      if (problemCode(e) === 'switch_over_changed') await this.loadPreview();
    } finally {
      this.busy.set(false);
    }
  }

  private take(s: FixedAssetSettings): void {
    this.settings.set(s);
    this.draft = {
      treatment: s.treatment,
      method: s.method,
      defaultUsefulLifeMonths: s.defaultUsefulLifeMonths,
      residualPercent: s.residualPercent,
      openingValue: s.openingValue,
      switchMonth: s.switchMonth ?? '',
      switchToStraightLine: s.switchToStraightLine ?? true,
    };
    this.lifeDraft = Object.fromEntries(s.lives.map((l) => [l.productId, l.usefulLifeMonths]));
    this.methodDraft = Object.fromEntries(
      s.lives.map((l) => [
        l.productId,
        {
          method: l.method ?? '',
          coefficient: l.decliningCoefficient ?? null,
          switch: l.switchToStraightLine === true ? 'on' : l.switchToStraightLine === false ? 'off' : '',
        } satisfies MethodDraft,
      ]),
    );
    this.mapDraft = Object.fromEntries(s.slots.map((m) => [m.slot, m.ledgerAccountId]));
  }

  private message(e: unknown): string {
    const code = problemCode(e);
    return code && REFUSALS[code] ? this.i18n.t(REFUSALS[code]) : adminErrorMessage(e);
  }

  /** A blocking reason in the reader's language; the server's words for one this page does not know. */
  blockingText(code: string, message: string): string {
    return BLOCKING.has(code) ? this.i18n.t('fa.blocking.' + code) : message;
  }

  /** The type is depreciated on the declining balance as drafted: its own method, or the company's when it follows it. */
  declines(l: FixedAssetLife): boolean {
    const own = this.methodDraft[l.productId]?.method || null;
    return (own ?? this.draft.method) === 'DecliningBalance';
  }

  /** The tax code's coefficient for the type's life, in the company's number format; '—' without a life. */
  suggested(l: FixedAssetLife): string {
    const life = this.lifeDraft[l.productId] || this.draft.defaultUsefulLifeMonths;
    const value = legalCoefficient(life || null) ?? l.suggestedCoefficient ?? null;
    return value === null ? '—' : formatNumber(value, this.i18n.lang(), { minimumFractionDigits: 1, maximumFractionDigits: 3 });
  }

  lifeName(l: FixedAssetLife): string {
    return localizedName(l, this.i18n.lang()) || l.sku;
  }

  lineName(l: { nameAr: string; nameFr: string; nameEn: string; sku: string }): string {
    return localizedName(l, this.i18n.lang()) || l.sku;
  }

  lineBranch(l: { branchNameAr: string; branchNameFr: string; branchNameEn: string; branchCode: string }): string {
    return localizedName({ nameAr: l.branchNameAr, nameFr: l.branchNameFr, nameEn: l.branchNameEn }, this.i18n.lang()) || l.branchCode;
  }

  accountName(a: LedgerAccount): string {
    return localizedName(a, this.i18n.lang());
  }

  slotAccountName(m: FixedAssetSlot): string {
    return localizedName({ nameAr: m.accountNameAr ?? '', nameFr: m.accountNameFr ?? '', nameEn: m.accountNameEn ?? '' }, this.i18n.lang());
  }
}
