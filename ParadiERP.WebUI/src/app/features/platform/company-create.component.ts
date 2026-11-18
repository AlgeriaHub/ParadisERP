import { Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../core/i18n.service';
import { CreatedCompany, NewCompany, PlatformService } from '../../core/platform.service';
import { platformText } from './platform-text';
import { ConfirmService } from '../../ui/confirm.service';
import { actionRequest } from '../../ui/confirm-requests';
import { PasswordVisibilityDirective } from '../../ui/password-visibility.directive';

/**
 * Creates a company from the portal. The administrator's one-time password is shown exactly
 * once, from this component's memory only; it is dropped as soon as the operator is done.
 */
@Component({
  selector: 'app-company-create',
  standalone: true,
  imports: [FormsModule, PasswordVisibilityDirective],
  template: `
    @if (created(); as result) {
      <section class="one-time" role="status">
        <h3>{{ text().oneTimeTitle }}</h3>
        <p class="notice danger">{{ text().oneTimeHint }}</p>
        <dl>
          <dt>{{ text().companyId }}</dt><dd class="mono">{{ result.id }}</dd>
          <dt>{{ text().adminUsername }}</dt><dd class="mono">{{ result.adminUsername }}</dd>
        </dl>
        <div class="secret">
          <code class="mono" data-testid="one-time-password">{{ result.bootstrapPassword }}</code>
          <button type="button" class="btn-ghost" (click)="copy(result.bootstrapPassword)">{{ copied() ? text().copied : text().copy }}</button>
        </div>
        <button type="button" class="btn-primary" (click)="finish(result)">{{ text().done }}</button>
      </section>
    } @else {
      <form (ngSubmit)="submit()">
        <fieldset [disabled]="busy()">
          <h3>{{ text().createTitle }}</h3>
          <p class="muted">{{ text().createHint }}</p>
          <div class="fields">
            <label>{{ text().companyId }}<input name="companyId" [(ngModel)]="id" pattern="[a-z0-9][a-z0-9-]{1,38}[a-z0-9]" maxlength="40" required /></label>
            <label>{{ text().adminUsername }}<input name="adminUsername" [(ngModel)]="adminUsername" maxlength="64" required /></label>
          </div>
          <label>{{ text().hostnames }}<textarea name="hosts" rows="2" [(ngModel)]="hosts" required></textarea></label>
          <div class="fields">
            <label>{{ text().databaseProvider }}
              <select name="provider" [(ngModel)]="provider">
                <option value="Sqlite">SQLite</option>
                <option value="SqlServer">SQL Server</option>
              </select>
            </label>
            <label>{{ text().country }}
              <select name="country" [(ngModel)]="country">
                @for (code of countries(); track code) { <option [value]="code">{{ code }}</option> }
              </select>
            </label>
          </div>
          @if (provider === 'Sqlite') {
            <p class="muted">{{ text().sqliteHint }}</p>
          } @else {
            <label>{{ text().connectionString }}<input name="connectionString" type="password" autocomplete="off" [(ngModel)]="connectionString" required /></label>
          }
          <div class="fields">
            <label>{{ text().plan }}<input name="newPlan" [(ngModel)]="plan" maxlength="100" required /></label>
            <label>{{ text().status }}
              <select name="newStatus" [(ngModel)]="status">
                <option value="Trial">{{ i18n.t('subscription.status.Trial') }}</option>
                <option value="Active">{{ i18n.t('subscription.status.Active') }}</option>
              </select>
            </label>
            <label>{{ text().expiry }}<input name="newExpiry" type="datetime-local" [(ngModel)]="expiry" required /></label>
            <label>{{ text().grace }}<input name="newGrace" type="number" min="0" max="30" step="1" [(ngModel)]="graceDays" required /></label>
          </div>
          <label>{{ text().reason }}<input name="createReason" [(ngModel)]="reason" maxlength="500" required /></label>
          <div class="row">
            <button type="submit" class="btn-primary" [disabled]="!canSubmit()">{{ text().createCompany }}</button>
            <button type="button" class="btn-ghost" (click)="cancelled.emit()">{{ text().cancel }}</button>
          </div>
        </fieldset>
      </form>
    }
  `,
  styles: [
    `
      :host { --pt-ink-2: var(--muted); }
      fieldset { border: 0; padding: 0; margin: 0; display: grid; gap: 0.9rem; }
      label { display: grid; gap: 0.35rem; font-size: 0.86rem; margin: 0; color: var(--text); }
      h3 { margin: 0; font-size: 1.15rem; }
      p { margin: 0; line-height: 1.6; max-width: 68ch; }
      p.muted { color: var(--pt-ink-2); }
      .fields { display: grid; grid-template-columns: repeat(auto-fit, minmax(13rem, 1fr)); gap: 0.75rem; }
      .row { display: flex; gap: 0.6rem; flex-wrap: wrap; }
      input, select, textarea { width: 100%; min-width: 0; }
      textarea { font: inherit; resize: vertical; }
      .one-time { display: grid; gap: 0.9rem; }
      .one-time > button { justify-self: start; }
      dl {
        display: grid;
        grid-template-columns: max-content minmax(0, 1fr);
        gap: 0.3rem 1rem;
        margin: 0;
        padding: 0.8rem;
        background: var(--surface-2);
        border-radius: var(--radius);
      }
      dt { color: var(--pt-ink-2); font-size: 0.86rem; }
      dd { margin: 0; overflow-wrap: anywhere; font-weight: 600; }
      /* The password is shown once and is meant to be read aloud or copied — it is the
         largest thing on the panel for exactly as long as it exists. */
      .secret { display: flex; gap: 0.6rem; align-items: center; flex-wrap: wrap; }
      .secret code {
        font-size: 1.2rem;
        font-weight: 700;
        letter-spacing: 0.02em;
        padding: 0.6rem 0.85rem;
        background: var(--surface-2);
        border: 1px dashed var(--border);
        border-radius: var(--radius);
        user-select: all;
        overflow-wrap: anywhere;
      }
    `,
  ],
})
export class CompanyCreateComponent {
  readonly i18n = inject(I18nService);
  private readonly api = inject(PlatformService);
  private readonly confirm = inject(ConfirmService);

  /** Country preset codes offered by the server. */
  readonly countries = input<string[]>([]);
  readonly created = signal<CreatedCompany | null>(null);
  readonly busy = signal(false);
  readonly copied = signal(false);
  /** Emitted after the operator has seen the password; the page then reloads and selects the company. */
  readonly finished = output<string>();
  readonly cancelled = output<void>();
  readonly failed = output<unknown>();
  readonly text = computed(() => platformText(this.i18n.lang()));

  id = '';
  hosts = '';
  provider: NewCompany['databaseProvider'] = 'Sqlite';
  connectionString = '';
  country = 'DZ';
  adminUsername = 'admin';
  plan = 'Trial';
  status = 'Trial';
  expiry = new Date(Date.now() + 30 * 86_400_000).toISOString().slice(0, 16);
  graceDays = 7;
  reason = '';

  canSubmit(): boolean {
    return !!this.id.trim() && !!this.hosts.trim() && !!this.adminUsername.trim() && !!this.reason.trim() && !!this.expiry
      && (this.provider === 'Sqlite' || !!this.connectionString.trim());
  }

  async submit(): Promise<void> {
    if (!this.canSubmit() || this.busy()) return;
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.text().createCompany)))) return;
    this.busy.set(true);
    try {
      const result = await this.api.createCompany({
        id: this.id.trim().toLowerCase(),
        hosts: this.hosts.split(/[\s,;]+/).map((h) => h.trim()).filter(Boolean),
        databaseProvider: this.provider,
        connectionString: this.provider === 'Sqlite' ? null : this.connectionString.trim(),
        country: this.country,
        adminUsername: this.adminUsername.trim(),
        subscription: {
          plan: this.plan.trim(), status: this.status, expiresAtUtc: new Date(this.expiry + 'Z').toISOString(),
          graceDays: Number(this.graceDays), maxUsers: null, maxBranches: null, maxDevices: null,
        },
        reason: this.reason.trim(),
      });
      this.connectionString = '';
      this.created.set(result);
    } catch (error) {
      this.failed.emit(error);
    } finally {
      this.busy.set(false);
    }
  }

  async copy(value: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(value);
      this.copied.set(true);
    } catch {
      /* the text is selectable; copying by hand still works */
    }
  }

  finish(result: CreatedCompany): void {
    this.created.set(null);
    this.copied.set(false);
    this.finished.emit(result.id);
  }
}
