import { Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService, LANGS } from '../../core/i18n.service';
import { CashRoundingMode, Lang, PaymentMethod, ReceiptSection, RegionalSettings } from '../../core/models';
import { UI_FONTS } from '../../core/ui-fonts';
import { platformText } from './platform-text';
import { ConfirmService } from '../../ui/confirm.service';
import { removeLineRequest, saveRequest } from '../../ui/confirm-requests';
import { DEFAULT_NUMBER_FORMAT, NUMBER_FORMATS, formatNumber, isNumberFormat } from '../../core/number-format';

export interface RegionalSave {
  settings: RegionalSettings;
  reason: string;
}

export interface LanguageChoice {
  code: Lang;
  label: string;
}

const METHODS: PaymentMethod[] = ['Cash', 'Card', 'Credit', 'GiftCard', 'Loyalty'];
const MODES: CashRoundingMode[] = ['Nearest', 'Up', 'Down'];
const SECTIONS: ReceiptSection[] = ['header', 'fiscalIds', 'meta', 'lines', 'totals', 'taxBreakdown', 'payment', 'legal', 'footer'];
const REQUIRED_SECTIONS: ReceiptSection[] = ['lines', 'totals'];

/** Operator form for one company's country profile. The server re-validates everything. */
@Component({
  selector: 'app-regional-editor',
  standalone: true,
  imports: [FormsModule],
  template: `
    @if (form(); as f) {
      <form (ngSubmit)="submit()">
        <fieldset [disabled]="busy()">
          <p class="muted">{{ text().regionalHint }}</p>

          <div class="preset">
            <label>{{ text().preset }}
              <select name="preset" [(ngModel)]="preset">
                @for (code of presetCodes(); track code) { <option [value]="code">{{ code }} — {{ presets()[code].currencyCode }}</option> }
              </select>
            </label>
            <button type="button" class="btn-ghost" [disabled]="!preset" (click)="applyPreset()">{{ text().applyPreset }}</button>
          </div>

          <h4>{{ text().languageSection }}</h4>
          <div class="checks">
            @for (lang of choices(); track lang.code) {
              <label class="check"><input type="checkbox" [name]="'lang-' + lang.code" [ngModel]="f.languages.includes(lang.code)" (ngModelChange)="toggleLang(lang.code, $event)" />{{ lang.label }}</label>
            }
          </div>
          <label>{{ text().defaultLanguage }}
            <select name="defaultLanguage" [(ngModel)]="f.defaultLanguage">
              @for (lang of enabledLangs(); track lang.code) { <option [value]="lang.code">{{ lang.label }}</option> }
            </select>
          </label>
          <label>{{ text().uiFont }}
            <select name="uiFont" [(ngModel)]="f.uiFont">
              @for (font of fonts; track font.key) { <option [value]="font.key">{{ font.label || text().uiFontSystem }}</option> }
            </select>
          </label>
          <p class="muted hint">{{ text().uiFontHint }}</p>

          <h4>{{ text().currencySection }}</h4>
          <div class="fields three">
            <label>{{ text().country }}<input name="country" [(ngModel)]="f.countryCode" maxlength="2" required /></label>
            <label>{{ text().currency }}<input name="currency" [(ngModel)]="f.currencyCode" maxlength="3" required /></label>
            <label>{{ text().decimals }}<input name="decimals" type="number" min="0" max="3" step="1" [(ngModel)]="f.currencyDecimals" required /></label>
            @for (lang of enabledLangs(); track lang.code) {
              <label>{{ text().symbol }} ({{ lang.label }})<input [name]="'symbol-' + lang.code" [(ngModel)]="f.currencySymbols[lang.code]" maxlength="10" /></label>
            }
          </div>
          <label class="check"><input name="symbolFirst" type="checkbox" [(ngModel)]="f.currencySymbolFirst" />{{ text().symbolFirst }}</label>
              <label>{{ text().numberFormat }}
                <select name="numberFormat" [(ngModel)]="f.numberFormat">
                  @for (n of numberFormats; track n) {
                    <option [value]="n">{{ n === 'locale' ? text().numberFormatLocale : n }}</option>
                  }
                </select>
              </label>
          <div class="fields">
            <label>{{ text().timeZone }}<input name="timeZone" [(ngModel)]="f.timeZone" required placeholder="Africa/Algiers" /></label>
            <label>{{ text().denominations }}<input name="denominations" [(ngModel)]="denominations" placeholder="100, 200, 500" /></label>
            <label>{{ text().rounding }}<input name="rounding" type="number" min="0" [step]="moneyStep()" [(ngModel)]="f.cashRoundingIncrement" /></label>
            <label>{{ text().roundingMode }}
              <select name="roundingMode" [(ngModel)]="f.cashRoundingMode" [disabled]="!f.cashRoundingIncrement">
                @for (mode of modes; track mode) { <option [value]="mode">{{ modeLabel(mode) }}</option> }
              </select>
            </label>
          </div>
          <p class="muted sample">
            <span>{{ text().sample }}</span>
            <span class="mono">{{ sampleMoney() }}</span>
            <span class="mono">{{ sampleTime() }}</span>
          </p>

          <h4>{{ text().taxSection }}</h4>
          <div class="fields three">
            @for (lang of enabledLangs(); track lang.code) {
              <label>{{ text().taxName }} ({{ lang.label }})<input [name]="'tax-' + lang.code" [(ngModel)]="f.taxNames[lang.code]" maxlength="60" /></label>
            }
          </div>
          <div class="fields">
            <label>{{ text().taxRates }}<input name="rates" [(ngModel)]="rates" (ngModelChange)="syncRates()" placeholder="20, 10, 5.5, 0" required /></label>
            <label>{{ text().defaultTaxRate }}
              <select name="defaultRate" [(ngModel)]="f.defaultTaxRate">
                @for (rate of parsedRates(); track rate) { <option [ngValue]="rate">{{ rate }}%</option> }
              </select>
            </label>
          </div>
          <label class="check"><input name="inclusive" type="checkbox" [(ngModel)]="f.pricesIncludeTax" />{{ text().pricesIncludeTax }}</label>
          @if (parsedRates().length) {
            <p class="subhead">{{ text().rateLabels }}</p>
            @for (rate of parsedRates(); track rate) {
              <div class="rate-row">
                <span class="mono rate">{{ rate }}%</span>
                <div class="fields three">
                  @for (lang of enabledLangs(); track lang.code) {
                    <label>{{ lang.label }}<input [name]="'rate-' + rate + '-' + lang.code" [(ngModel)]="rateLabel(rate)[lang.code]" maxlength="60" /></label>
                  }
                </div>
              </div>
            }
          }

          <h4>{{ text().fiscalSection }}</h4>
          <p class="muted">{{ text().fiscalHint }}</p>
          @for (id of f.fiscalIds; track $index; let i = $index) {
            <div class="fiscal">
              <div class="fields three">
                <label>{{ text().fiscalKey }}<input [name]="'key-' + i" [(ngModel)]="id.key" maxlength="20" required /></label>
                <label>{{ text().fiscalCode }}<input [name]="'code-' + i" [(ngModel)]="id.code" maxlength="20" required /></label>
                @for (lang of enabledLangs(); track lang.code) {
                  <label>{{ text().label }} ({{ lang.label }})<input [name]="'label-' + i + lang.code" [(ngModel)]="id.labels[lang.code]" maxlength="60" /></label>
                }
              </div>
              <div class="row">
                <label class="check"><input type="checkbox" [name]="'req-' + i" [(ngModel)]="id.required" />{{ text().required }}</label>
                <label class="check"><input type="checkbox" [name]="'rcpt-' + i" [(ngModel)]="id.showOnReceipt" />{{ text().onReceipt }}</label>
                <button type="button" class="btn-danger small" (click)="removeFiscal(i)">{{ text().remove }}</button>
              </div>
            </div>
          }
          <button type="button" class="btn-ghost" [disabled]="f.fiscalIds.length >= 10" (click)="addFiscal()">{{ text().addFiscal }}</button>
          <div class="fields three">
            @for (lang of enabledLangs(); track lang.code) {
              <label>{{ text().customerTaxId }} ({{ lang.label }})<input [name]="'ctax-' + lang.code" [(ngModel)]="f.customerTaxIdLabels[lang.code]" maxlength="60" /></label>
            }
          </div>

          <h4>{{ text().receiptSection }}</h4>
          <p class="muted">{{ text().receiptHint }}</p>
          <ol class="sections">
            @for (section of sectionOrder(); track section; let i = $index, last = $last, first = $first) {
              <li>
                <label class="check">
                  <input type="checkbox" [name]="'sec-' + section" [ngModel]="f.receiptSections.includes(section)"
                    [disabled]="isRequired(section)" (ngModelChange)="toggleSection(section, $event)" />
                  {{ sectionLabel(section) }}
                </label>
                <span class="arrows">
                  <button type="button" class="btn-ghost small" [disabled]="first" [attr.aria-label]="text().moveUp" (click)="moveSection(i, -1)">↑</button>
                  <button type="button" class="btn-ghost small" [disabled]="last" [attr.aria-label]="text().moveDown" (click)="moveSection(i, 1)">↓</button>
                </span>
              </li>
            }
          </ol>
          @for (lang of enabledLangs(); track lang.code) {
            <label>{{ text().legalText }} ({{ lang.label }})
              <textarea [name]="'legal-' + lang.code" rows="2" maxlength="500" [(ngModel)]="f.receiptLegalText[lang.code]"></textarea>
            </label>
          }

          <h4>{{ text().paymentSection }}</h4>
          <div class="checks">
            @for (method of methods; track method) {
              <label class="check"><input type="checkbox" [name]="'pay-' + method" [ngModel]="f.paymentMethods.includes(method)" (ngModelChange)="toggleMethod(method, $event)" />{{ i18n.t('pay.' + method.toLowerCase()) }}</label>
            }
          </div>

          @if (currencyChanged()) { <p class="notice danger">{{ text().currencyWarning }}</p> }
          @if (problem()) { <p class="notice danger" role="alert">{{ problem() }}</p> }
          <label>{{ text().reason }}<input name="regionalReason" [(ngModel)]="reason" maxlength="500" required /></label>
          <button type="submit" class="btn-primary" [disabled]="!reason.trim()">{{ text().saveRegional }}</button>
        </fieldset>
      </form>
    }
  `,
  styles: [
    `
      :host { --pt-ink-2: var(--muted); }
      fieldset { border: 0; padding: 0; margin: 0; display: grid; gap: 0.9rem; }
      label { display: grid; gap: 0.35rem; font-size: 0.86rem; margin: 0; color: var(--text); }
      label.check { display: flex; align-items: center; gap: 0.5rem; }
      label.check input { width: auto; flex: none; }
      /* Every block of the profile opens with the same eyebrow over a rule, so a form this
         long reads as a stack of decisions rather than one wall of fields. */
      h4 {
        margin: 0.9rem 0 0;
        padding-block-start: 0.9rem;
        border-top: 1px solid var(--border);
        font-size: 0.78rem;
        font-weight: 600;
        letter-spacing: 0.06em;
        text-transform: uppercase;
        color: var(--pt-ink-2);
      }
      .subhead { margin: 0; font-size: 0.82rem; font-weight: 600; color: var(--pt-ink-2); }
      p.muted, .hint, .sample { margin: 0; color: var(--pt-ink-2); line-height: 1.6; max-width: 68ch; }
      .sample { display: flex; flex-wrap: wrap; gap: 0.1rem 0.8rem; }
      /* Auto-fit rather than a fixed count: the same rule serves a phone and a wide laptop,
         and the number of language columns is not known until the profile is loaded. */
      .fields { display: grid; grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr)); gap: 0.75rem; }
      .fields.three { grid-template-columns: repeat(auto-fit, minmax(10rem, 1fr)); }
      .checks, .row { display: flex; flex-wrap: wrap; gap: 0.5rem 1.1rem; align-items: center; }
      .preset { display: flex; flex-wrap: wrap; gap: 0.75rem; align-items: end; }
      .preset label { flex: 1 1 14rem; }
      .fiscal { border: 1px solid var(--border); border-radius: var(--radius); padding: 0.85rem; display: grid; gap: 0.75rem; background: var(--surface-2); }
      .rate-row { display: grid; grid-template-columns: 4rem minmax(0, 1fr); gap: 0.75rem; align-items: end; }
      .rate-row .rate { padding-block-end: 0.6rem; font-weight: 600; }
      .sections { margin: 0; padding: 0; list-style: none; display: grid; gap: 0.3rem; }
      .sections li {
        display: flex; justify-content: space-between; align-items: center; gap: 0.75rem;
        background: var(--surface-2); border-radius: var(--radius); padding: 0.4rem 0.6rem;
      }
      .sections li label { font-size: 0.88rem; }
      .arrows { display: flex; gap: 0.3rem; flex: none; }
      .arrows button { padding: 0.15rem 0.5rem; font-size: 0.9rem; line-height: 1.2; }
      input, select, textarea { width: 100%; min-width: 0; }
      textarea { font: inherit; resize: vertical; }
      fieldset > button { justify-self: start; }
      @media (max-width: 40rem) {
        .preset { flex-direction: column; align-items: stretch; }
        .rate-row { grid-template-columns: 1fr; }
        .rate-row .rate { padding-block-end: 0; }
      }
    `,
  ],
})
export class RegionalEditorComponent {
  readonly i18n = inject(I18nService);
  private readonly confirm = inject(ConfirmService);
  readonly current = input.required<RegionalSettings>();
  readonly presets = input<Record<string, RegionalSettings>>({});
  /** Uploaded languages that can be enabled in addition to the built-in ones. */
  readonly languagePacks = input<LanguageChoice[]>([]);
  readonly busy = input(false);
  readonly saved = output<RegionalSave>();

  readonly text = computed(() => platformText(this.i18n.lang()));
  /** How figures are written (core/number-format.ts); 1,234.56 is the system's own. */
  readonly numberFormats = NUMBER_FORMATS;
  /** Straight from the shared catalogue, so a new face needs no change here. */
  readonly fonts = UI_FONTS;
  readonly choices = computed<LanguageChoice[]>(() => [...LANGS, ...this.languagePacks()]);
  readonly methods = METHODS;
  readonly modes = MODES;
  readonly presetCodes = computed(() => Object.keys(this.presets()));
  readonly form = signal<RegionalSettings | null>(null);
  readonly problem = signal('');
  /** Every receipt block in display order: the enabled ones first, as the profile lists them. */
  readonly sectionOrder = signal<ReceiptSection[]>([]);

  preset = '';
  rates = '';
  denominations = '';
  reason = '';
  private rateLabels: Record<string, Record<Lang, string | undefined>> = {};

  constructor() {
    effect(() => this.load(this.current()));
  }

  enabledLangs(): LanguageChoice[] {
    const f = this.form();
    if (!f) return [];
    // Keep a language the profile enables visible even if its pack was deleted meanwhile.
    return f.languages.map((code) => this.choices().find((c) => c.code === code) ?? { code, label: code });
  }

  moneyStep(): number {
    return 1 / 10 ** (Number(this.form()?.currencyDecimals) || 0);
  }

  parsedRates(): number[] {
    return parseNumbers(this.rates);
  }

  rateLabel(rate: number): Record<Lang, string | undefined> {
    return (this.rateLabels[String(rate)] ??= {});
  }

  modeLabel(mode: CashRoundingMode): string {
    const t = this.text();
    return mode === 'Up' ? t.modeUp : mode === 'Down' ? t.modeDown : t.modeNearest;
  }

  sectionLabel(section: ReceiptSection): string {
    return (this.text() as Record<string, string>)['sec_' + section] ?? section;
  }

  isRequired(section: ReceiptSection): boolean {
    return REQUIRED_SECTIONS.includes(section);
  }

  currencyChanged(): boolean {
    const f = this.form();
    return !!f && f.currencyCode.trim().toUpperCase() !== this.current().currencyCode;
  }

  /**
   * The preview is two separate facts, what money looks like and what the clock says, so it
   * is built as two and rendered as two elements with a gap between them. Joined by a middle
   * dot it read wrongly in Arabic: a neutral glyph between two runs resolves to the
   * paragraph's direction and lands on the far side of the figure it was meant to follow.
   */
  private sample(): { money: string; time: string } {
    const f = this.form();
    if (!f) return { money: '', time: '' };
    const lang = f.languages.includes(this.i18n.lang()) ? this.i18n.lang() : f.defaultLanguage;
    const decimals = Math.min(3, Math.max(0, Number(f.currencyDecimals) || 0));
    // The figure as this company will see it, with the number format chosen here.
    const amount = formatNumber(1234.5, `${lang}-${f.countryCode}`, { minimumFractionDigits: decimals, maximumFractionDigits: decimals },
      isNumberFormat(f.numberFormat) ? f.numberFormat : DEFAULT_NUMBER_FORMAT);
    const symbol = f.currencySymbols[lang] || f.currencyCode;
    let time: string;
    try {
      time = new Intl.DateTimeFormat(`${lang}-${f.countryCode}`, { timeStyle: 'short', timeZone: f.timeZone }).format(new Date());
    } catch {
      time = '—';
    }
    return { money: f.currencySymbolFirst ? `${symbol} ${amount}` : `${amount} ${symbol}`, time };
  }

  sampleMoney(): string {
    return this.sample().money;
  }

  sampleTime(): string {
    return this.sample().time;
  }

  applyPreset(): void {
    const preset = this.presets()[this.preset];
    if (preset) this.load(preset);
  }

  toggleLang(lang: Lang, on: boolean): void {
    const f = this.form();
    if (!f) return;
    f.languages = this.choices()
      .map((c) => c.code)
      .filter((c) => (c === lang ? on : f.languages.includes(c)));
    if (!f.languages.includes(f.defaultLanguage) && f.languages.length) f.defaultLanguage = f.languages[0];
  }

  toggleMethod(method: PaymentMethod, on: boolean): void {
    const f = this.form();
    if (!f) return;
    f.paymentMethods = METHODS.filter((m) => (m === method ? on : f.paymentMethods.includes(m)));
  }

  toggleSection(section: ReceiptSection, on: boolean): void {
    const f = this.form();
    if (!f) return;
    f.receiptSections = this.sectionOrder().filter((s) => (s === section ? on : f.receiptSections.includes(s)));
  }

  moveSection(index: number, delta: number): void {
    const f = this.form();
    if (!f) return;
    const order = [...this.sectionOrder()];
    [order[index], order[index + delta]] = [order[index + delta], order[index]];
    this.sectionOrder.set(order);
    f.receiptSections = order.filter((s) => f.receiptSections.includes(s));
  }

  syncRates(): void {
    const f = this.form();
    const rates = this.parsedRates();
    if (f && rates.length && !rates.includes(f.defaultTaxRate)) f.defaultTaxRate = rates[0];
  }

  addFiscal(): void {
    this.form()?.fiscalIds.push({ key: '', code: '', labels: {}, required: false, showOnReceipt: true });
  }

  /** Asks first only when the identifier holds something typed; an empty new row just goes. */
  async removeFiscal(index: number): Promise<void> {
    const row = this.form()?.fiscalIds[index];
    if (!row) return;
    const typed = !!row.key.trim() || !!row.code.trim() || Object.values(row.labels ?? {}).some((text) => !!text?.trim());
    if (typed && !(await this.confirm.ask(removeLineRequest(this.i18n)))) return;
    this.form()?.fiscalIds.splice(index, 1);
  }

  async submit(): Promise<void> {
    const f = this.form();
    if (!f || this.busy() || !this.reason.trim()) return;
    this.problem.set('');
    try {
      new Intl.DateTimeFormat('en', { timeZone: f.timeZone.trim() });
    } catch {
      this.problem.set(this.text().badTimeZone);
      return;
    }
    const rates = this.parsedRates();
    if (!rates.length) {
      this.problem.set(this.text().badRates);
      return;
    }
    if (!(await this.confirm.ask(saveRequest(this.i18n)))) return;
    const settings: RegionalSettings = {
      ...f,
      countryCode: f.countryCode.trim().toUpperCase(),
      currencyCode: f.currencyCode.trim().toUpperCase(),
      currencyDecimals: Number(f.currencyDecimals),
      cashRoundingIncrement: Number(f.cashRoundingIncrement) || 0,
      timeZone: f.timeZone.trim(),
      taxRates: rates,
      taxRateLabels: rates.map((rate) => ({ rate, labels: { ...this.rateLabel(rate) } })),
      cashDenominations: parseNumbers(this.denominations),
      fiscalIds: f.fiscalIds.map((id) => ({ ...id, key: id.key.trim().toLowerCase(), code: id.code.trim() })),
    };
    this.saved.emit({ settings, reason: this.reason.trim() });
  }

  private load(source: RegionalSettings): void {
    const copy = structuredClone(source);
    copy.taxRateLabels ??= [];
    copy.receiptSections = copy.receiptSections?.length ? copy.receiptSections : [...SECTIONS];
    copy.receiptLegalText ??= {};
    copy.cashRoundingIncrement ??= 0;
    copy.cashRoundingMode ??= 'Nearest';
    this.form.set(copy);
    this.rates = copy.taxRates.join(', ');
    this.denominations = copy.cashDenominations.join(', ');
    this.rateLabels = Object.fromEntries(copy.taxRateLabels.map((l) => [String(l.rate), { ...l.labels }]));
    this.sectionOrder.set([...copy.receiptSections, ...SECTIONS.filter((s) => !copy.receiptSections.includes(s))]);
    this.problem.set('');
  }
}

/** "20, 10, 5.5" → [20, 10, 5.5]. Decimals use a dot because a comma separates values. */
function parseNumbers(value: string): number[] {
  return value
    .split(/[,;\s]+/)
    .filter(Boolean)
    .map(Number)
    .filter((n) => Number.isFinite(n) && n >= 0);
}
