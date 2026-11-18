import { Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../core/i18n.service';
import { CompanyIntegrations, IntegrationSecretChanges, IntegrationSettings, PlatformService } from '../../core/platform.service';
import { platformText } from './platform-text';
import { ConfirmService } from '../../ui/confirm.service';
import { saveRequest } from '../../ui/confirm-requests';
import { PasswordVisibilityDirective } from '../../ui/password-visibility.directive';

/**
 * Operator form for one company's integrations. Secrets are write-only: the form only learns
 * whether one is stored, an empty field keeps it, and "remove" clears it.
 */
@Component({
  selector: 'app-integrations-editor',
  standalone: true,
  imports: [FormsModule, PasswordVisibilityDirective],
  template: `
    @if (form(); as f) {
      <form (ngSubmit)="save()">
        <fieldset [disabled]="busy() || working()">
          <p class="muted">{{ text().integrationsHint }}</p>

          <h4>{{ text().cardSection }}</h4>
          <label>{{ text().cardMode }}
            <select name="cardMode" [(ngModel)]="f.card.mode">
              <option value="None">{{ text().cardNone }}</option>
              <option value="ExternalTerminal">{{ text().cardExternal }}</option>
            </select>
          </label>
          <label class="check">
            <input type="checkbox" name="cardRef" [(ngModel)]="f.card.requireCardReference" [disabled]="f.card.mode === 'None'" />
            {{ text().cardRequireReference }}
          </label>

          <h4>{{ text().smsSection }}</h4>
          <label class="check"><input type="checkbox" name="smsOn" [(ngModel)]="f.sms.enabled" />{{ text().enabled }}</label>
          @if (f.sms.enabled) {
            <div class="fields">
              <label>{{ text().gatewayUrl }}<input name="smsUrl" [(ngModel)]="f.sms.url" placeholder="https://sms.example.com/send" required /></label>
              <label>{{ text().httpMethod }}
                <select name="smsMethod" [(ngModel)]="f.sms.method">
                  <option value="POST">POST</option><option value="PUT">PUT</option><option value="GET">GET</option>
                </select>
              </label>
              <label>{{ text().tokenHeader }}<input name="smsHeader" [(ngModel)]="f.sms.tokenHeader" /></label>
              <label>{{ secretLabel(text().token, loaded()?.hasSmsToken) }}
                <input name="smsToken" type="password" autocomplete="new-password" [(ngModel)]="secrets.smsToken" />
              </label>
              <label>{{ text().contentType }}<input name="smsType" [(ngModel)]="f.sms.contentType" /></label>
            </div>
            @if (loaded()?.hasSmsToken) {
              <label class="check"><input type="checkbox" name="smsClear" [(ngModel)]="secrets.clearSmsToken" />{{ text().removeSecret }}</label>
            }
            <label>{{ text().bodyTemplate }}
              <textarea name="smsBody" rows="3" maxlength="2000" [(ngModel)]="f.sms.bodyTemplate"></textarea>
            </label>
            <p class="muted">{{ text().templateHint }}</p>
          }

          <h4>{{ text().emailSection }}</h4>
          <label class="check"><input type="checkbox" name="mailOn" [(ngModel)]="f.email.enabled" />{{ text().enabled }}</label>
          @if (f.email.enabled) {
            <div class="fields">
              <label>{{ text().smtpHost }}<input name="mailHost" [(ngModel)]="f.email.host" required /></label>
              <label>{{ text().smtpPort }}<input name="mailPort" type="number" min="1" max="65535" [(ngModel)]="f.email.port" required /></label>
              <label>{{ text().smtpUser }}<input name="mailUser" autocomplete="off" [(ngModel)]="f.email.user" /></label>
              <label>{{ secretLabel(text().smtpPassword, loaded()?.hasSmtpPassword) }}
                <input name="mailPassword" type="password" autocomplete="new-password" [(ngModel)]="secrets.smtpPassword" />
              </label>
              <label>{{ text().smtpFrom }}<input name="mailFrom" type="email" [(ngModel)]="f.email.from" required /></label>
            </div>
            <label class="check"><input type="checkbox" name="mailTls" [(ngModel)]="f.email.useTls" />{{ text().smtpTls }}</label>
            @if (loaded()?.hasSmtpPassword) {
              <label class="check"><input type="checkbox" name="mailClear" [(ngModel)]="secrets.clearSmtpPassword" />{{ text().removeSecret }}</label>
            }
          }

          <h4>{{ text().webhookSection }}</h4>
          <p class="muted">{{ text().webhookHint }}</p>
          <label class="check"><input type="checkbox" name="hookOn" [(ngModel)]="f.webhook.enabled" />{{ text().enabled }}</label>
          @if (f.webhook.enabled) {
            <div class="fields">
              <label>{{ text().webhookUrl }}<input name="hookUrl" [(ngModel)]="f.webhook.url" placeholder="https://erp.example.com/pos-events" required /></label>
              <label>{{ secretLabel(text().webhookSecret, loaded()?.hasWebhookSecret) }}
                <input name="hookSecret" type="password" autocomplete="new-password" minlength="16" [(ngModel)]="secrets.webhookSecret" />
              </label>
            </div>
          }
          @if (loaded()?.hasWebhookSecret) {
            <label class="check"><input type="checkbox" name="hookClear" [(ngModel)]="secrets.clearWebhookSecret" />{{ text().removeSecret }}</label>
          }
          @if (outboxLabel(); as label) { <p class="muted" role="status">{{ label }}</p> }
          @if (canTest()) {
            <button type="button" class="btn-ghost" (click)="testWebhook()">{{ text().testWebhook }}</button>
          }
          @if (testResult(); as r) { <p [class]="r.ok ? 'notice ok' : 'notice danger'" role="status">{{ r.text }}</p> }

          @if (problem()) { <p class="notice danger" role="alert">{{ problem() }}</p> }
          <label>{{ text().reason }}<input name="integrationsReason" [(ngModel)]="reason" maxlength="500" required /></label>
          <button type="submit" class="btn-primary" [disabled]="!reason.trim()">{{ text().saveIntegrations }}</button>
        </fieldset>
      </form>
    } @else if (problem()) {
      <p class="notice danger" role="alert">{{ problem() }}</p>
    }
  `,
  styles: [
    `
      :host { --pt-ink-2: var(--muted); }
      fieldset { border: 0; padding: 0; margin: 0; display: grid; gap: 0.9rem; }
      label { display: grid; gap: 0.35rem; font-size: 0.86rem; margin: 0; color: var(--text); }
      label.check { display: flex; align-items: center; gap: 0.5rem; }
      label.check input { width: auto; flex: none; }
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
      p { margin: 0; line-height: 1.6; max-width: 68ch; }
      p.muted { color: var(--pt-ink-2); }
      .fields { display: grid; grid-template-columns: repeat(auto-fit, minmax(13rem, 1fr)); gap: 0.75rem; }
      input, select, textarea { width: 100%; min-width: 0; }
      textarea { font: inherit; resize: vertical; }
      fieldset > button { justify-self: start; }
    `,
  ],
})
export class IntegrationsEditorComponent {
  private readonly api = inject(PlatformService);
  readonly i18n = inject(I18nService);
  private readonly confirm = inject(ConfirmService);

  readonly companyId = input.required<string>();
  readonly busy = input(false);
  readonly saved = output<void>();
  readonly failed = output<unknown>();

  readonly text = computed(() => platformText(this.i18n.lang()));
  readonly loaded = signal<CompanyIntegrations | null>(null);
  readonly form = signal<IntegrationSettings | null>(null);
  readonly working = signal(false);
  readonly problem = signal('');
  readonly testResult = signal<{ ok: boolean; text: string } | null>(null);
  readonly canTest = computed(() => {
    const loaded = this.loaded();
    return !!loaded?.settings.webhook.enabled && loaded.hasWebhookSecret;
  });

  secrets: IntegrationSecretChanges = {};
  reason = '';

  constructor() {
    effect(() => void this.load(this.companyId()));
  }

  secretLabel(label: string, stored: boolean | undefined): string {
    return stored ? `${label} (${this.text().secretStored})` : label;
  }

  outboxLabel(): string {
    const outbox = this.loaded()?.outbox;
    if (!outbox?.pending) return '';
    const t = this.text();
    const base = t.outboxPending.replace('{count}', String(outbox.pending));
    return outbox.lastError ? `${base} ${t.outboxLastError} ${outbox.lastError}` : base;
  }

  async save(): Promise<void> {
    const f = this.form();
    const loaded = this.loaded();
    if (!f || !loaded || this.working() || this.busy() || !this.reason.trim()) return;
    if (!(await this.confirm.ask(saveRequest(this.i18n)))) return;
    this.working.set(true);
    this.problem.set('');
    try {
      await this.api.saveIntegrations(this.companyId(), f, this.secrets, loaded.revision, this.reason.trim());
      await this.load(this.companyId());
      this.saved.emit();
    } catch (error) {
      this.show(error);
    } finally {
      this.working.set(false);
    }
  }

  async testWebhook(): Promise<void> {
    this.working.set(true);
    this.testResult.set(null);
    try {
      const result = await this.api.testWebhook(this.companyId());
      const t = this.text();
      this.testResult.set({ ok: result.ok, text: result.ok ? t.testOk : `${t.testFailed} ${result.error ?? ''}`.trim() });
    } catch (error) {
      this.show(error);
    } finally {
      this.working.set(false);
    }
  }

  private async load(companyId: string): Promise<void> {
    this.problem.set('');
    this.testResult.set(null);
    try {
      const loaded = await this.api.integrations(companyId);
      this.loaded.set(loaded);
      this.form.set(structuredClone(loaded.settings));
      this.secrets = {};
      this.reason = '';
    } catch (error) {
      this.show(error);
    }
  }

  /** Validation and conflict messages are shown here; session problems go to the portal. */
  private show(error: unknown): void {
    const status = (error as { status?: number })?.status;
    if (status === 400 || status === 409) this.problem.set((error as Error).message);
    else this.failed.emit(error);
  }
}
