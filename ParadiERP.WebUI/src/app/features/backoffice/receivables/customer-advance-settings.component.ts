import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { adminErrorMessage, problemCode } from '../../../core/admin.service';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { mayDo } from '../../../core/landing';
import { RegionalService, localized } from '../../../core/regional.service';
import { TableStateComponent } from '../../../ui/table-state.component';
import { localizedName } from '../../../core/multilingual';
import { AdvanceVat, AdvancesHeldOn, IncomeAccountChoice, KeptAdvances, ReceivableSettings, ReceivablesApi } from './receivables-api.service';

/** The refusals the panel says in the reader's language; anything else falls back to the server's sentence. */
const REFUSALS: Readonly<Record<string, string>> = {
  accounts_not_mapped: 'adv.settings.error.account',
  maintenance_advances_taken: 'adv.settings.error.maintenance',
};

/**
 * Finance › Settings › Customer advances (M7b; SAL-12, D45; owner, 2026-11-09: every accounting decision is a setting the
 * accountant sets, the default today's behaviour). Where an advance is held — the customer's account 411 (default) or
 * 419 until the invoice — and when its VAT is due — at the invoice (default) or when received, which only 419 offers.
 * Changed under accounting.manage; read-only otherwise. Company-wide.
 */
@Component({
  selector: 'app-customer-advance-settings',
  standalone: true,
  imports: [FormsModule, TableStateComponent],
  template: `
    @if (error()) {
      <p class="notice danger" role="alert">{{ error() }}</p>
    }
    @if (saved()) {
      <p class="notice ok" role="status">{{ i18n.t('adv.settings.saved') }}</p>
    }
    <section class="ui-surface" aria-labelledby="adv-title">
      <div class="ui-surface__head">
        <h2 id="adv-title">{{ i18n.t('adv.settings.title') }}</h2>
        <p>{{ i18n.t('adv.settings.lead') }}</p>
      </div>
      @if (loading() && !settings()) {
        <app-table-state kind="loading" />
      } @else if (settings(); as s) {
        <div class="body">
          <div class="grid">
            <div class="field">
              <label for="adv-held">{{ i18n.t('adv.settings.heldOn') }}</label>
              <select id="adv-held" [ngModel]="heldOn" (ngModelChange)="setHeldOn($event)" [disabled]="!canEdit() || busy()"
                aria-describedby="adv-held-help">
                @for (o of HELD; track o; let first = $first) {
                  <option [value]="o" [disabled]="o === 'AdvancesAccount' && !s.advancesAccountReady">
                    {{ i18n.t('adv.settings.heldOn.' + o) }}{{ first ? ' ' + i18n.t('adv.settings.default') : '' }}
                  </option>
                }
              </select>
              <small id="adv-held-help" class="help">SAL-12 · D45 · {{ i18n.t('adv.settings.heldOn.help') }}</small>
              @if (!s.advancesAccountReady) {
                <small class="help">{{ i18n.t('adv.settings.accountMissing') }}</small>
              }
            </div>
            <div class="field">
              <label for="adv-vat">{{ i18n.t('adv.settings.vat', { tax: taxName() }) }}</label>
              <select id="adv-vat" [ngModel]="vat" (ngModelChange)="vat = $event" [disabled]="!canEdit() || busy() || heldOn !== 'AdvancesAccount'"
                aria-describedby="adv-vat-help">
                @for (o of VAT; track o; let first = $first) {
                  <option [value]="o">{{ i18n.t('adv.settings.vat.' + o) }}{{ first ? ' ' + i18n.t('adv.settings.default') : '' }}</option>
                }
              </select>
              <small id="adv-vat-help" class="help">D45 · {{ i18n.t('adv.settings.vat.help', { tax: taxName() }) }}</small>
            </div>
            <div class="field">
              <label for="adv-kept">{{ i18n.t('adv.settings.kept') }}</label>
              <select id="adv-kept" [ngModel]="kept" (ngModelChange)="kept = $event" [disabled]="!canEdit() || busy()"
                aria-describedby="adv-kept-help">
                @for (o of KEPT; track o; let first = $first) {
                  <option [value]="o">{{ i18n.t('adv.settings.kept.' + o) }}{{ first ? ' ' + i18n.t('adv.settings.default') : '' }}</option>
                }
              </select>
              <small id="adv-kept-help" class="help">SAL-13 · {{ i18n.t('adv.settings.kept.help') }}</small>
            </div>
            <div class="field">
              <label for="adv-kept-account">
                {{ i18n.t('adv.settings.keptAccount') }}
                @if (kept === 'OtherIncome') {
                  <span class="bo-req">{{ i18n.t('common.required') }}</span>
                }
              </label>
              <select id="adv-kept-account" [ngModel]="keptAccount" (ngModelChange)="keptAccount = $event"
                [disabled]="!canEdit() || busy() || kept !== 'OtherIncome'" aria-describedby="adv-kept-account-help">
                <option [ngValue]="null">{{ i18n.t('adv.settings.keptAccount.none') }}</option>
                @for (a of s.incomeAccounts ?? []; track a.code) {
                  <option [ngValue]="a.code">{{ a.code }} — {{ accountName(a) }}</option>
                }
              </select>
              <small id="adv-kept-account-help" class="help">{{ i18n.t('adv.settings.keptAccount.help') }}</small>
            </div>
          </div>
          @if (s.openAdvances > 0) {
            <p class="ui-sub">{{ i18n.t('adv.settings.open', { amount: i18n.money(s.openAdvances) }) }}</p>
          }
          @if (canEdit()) {
            <div class="foot">
              @if (s.updatedAtUtc) {
                <span class="ui-sub">{{ i18n.t('adv.settings.updated', { date: i18n.dateTime(s.updatedAtUtc), who: s.updatedBy ?? '—' }) }}</span>
              }
              <button type="button" class="btn-primary" [disabled]="busy() || !dirty()" (click)="save()">{{ i18n.t('adv.settings.save') }}</button>
            </div>
          } @else {
            <p class="ui-sub">{{ i18n.t('adv.settings.readOnly') }}</p>
          }
        </div>
      }
    </section>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .ui-surface__head h2 {
        margin: 0;
        font-size: var(--fs-section);
        font-weight: 600;
      }
      .body {
        padding: var(--sp-4) var(--sp-5) var(--sp-5);
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
      .foot {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: flex-end;
        gap: var(--sp-3);
      }
    `,
  ],
})
export class CustomerAdvanceSettingsComponent implements OnInit {
  private readonly api = inject(ReceivablesApi);
  private readonly auth = inject(AuthService);
  private readonly regional = inject(RegionalService);
  readonly i18n = inject(I18nService);

  readonly HELD: readonly AdvancesHeldOn[] = ['CustomerAccount', 'AdvancesAccount'];
  readonly VAT: readonly AdvanceVat[] = ['AtInvoice', 'WhenReceived'];
  /** SAL-13: not allowed (the default, today) or kept as other income. */
  readonly KEPT: readonly KeptAdvances[] = ['NotAllowed', 'OtherIncome'];

  readonly settings = signal<ReceivableSettings | null>(null);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly saved = signal(false);
  readonly error = signal<string | null>(null);
  readonly canEdit = computed(() => mayDo(this.auth.currentUser(), 'accounting.manage'));

  heldOn: AdvancesHeldOn = 'CustomerAccount';
  vat: AdvanceVat = 'AtInvoice';
  kept: KeptAdvances = 'NotAllowed';
  keptAccount: string | null = null;

  async ngOnInit(): Promise<void> {
    this.loading.set(true);
    try {
      this.take(await this.api.settings());
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.loading.set(false);
    }
  }

  /** VAT when received is 419's only: back on 411 it returns to the invoice. */
  setHeldOn(value: AdvancesHeldOn): void {
    this.heldOn = value;
    if (value !== 'AdvancesAccount') this.vat = 'AtInvoice';
  }

  dirty(): boolean {
    const s = this.settings();
    return (
      !!s &&
      (s.advancesHeldOn !== this.heldOn ||
        s.advanceVat !== this.vat ||
        (s.keptAdvances ?? 'NotAllowed') !== this.kept ||
        (s.keptIncomeAccount ?? null) !== this.keptAccount)
    );
  }

  accountName(a: IncomeAccountChoice): string {
    return localizedName(a, this.i18n.lang());
  }

  taxName(): string {
    return localized(this.regional.settings().taxNames, this.i18n.lang());
  }

  async save(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.saved.set(false);
    try {
      this.take(
        await this.api.saveSettings({
          advancesHeldOn: this.heldOn,
          advanceVat: this.vat,
          keptAdvances: this.kept,
          keptIncomeAccount: this.keptAccount ?? '',
        }),
      );
      this.saved.set(true);
    } catch (e) {
      const code = problemCode(e) ?? '';
      // The same code says which account is missing: 419, or the income account a kept advance needs (SAL-13).
      const keptAccountMissing =
        code === 'accounts_not_mapped' && this.kept === 'OtherIncome' && !this.settings()?.incomeAccounts?.some((a) => a.code === this.keptAccount);
      const key = keptAccountMissing ? 'adv.settings.error.keptAccount' : REFUSALS[code];
      this.error.set(key ? this.i18n.t(key) : adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  private take(s: ReceivableSettings): void {
    this.settings.set(s);
    this.heldOn = s.advancesHeldOn;
    this.vat = s.advanceVat;
    this.kept = s.keptAdvances ?? 'NotAllowed';
    this.keptAccount = s.keptIncomeAccount ?? null;
  }
}
