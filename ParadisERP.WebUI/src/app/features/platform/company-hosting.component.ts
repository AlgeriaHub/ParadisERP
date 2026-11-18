import { Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../core/i18n.service';
import { PlatformCompany, PlatformService } from '../../core/platform.service';
import { platformText } from './platform-text';
import { ConfirmService } from '../../ui/confirm.service';
import { saveRequest } from '../../ui/confirm-requests';

/** Hostnames and archive state of a portal-created company; configuration companies are read-only. */
@Component({
  selector: 'app-company-hosting',
  standalone: true,
  imports: [FormsModule],
  template: `
    <h3>{{ text().hostingTitle }}</h3>
    @if (company().source === 'Configuration') {
      <p class="muted">{{ text().configuredHint }}</p>
    } @else {
      <p class="muted">{{ text().hostingHint }}</p>
      <form (ngSubmit)="saveHosts()">
        <fieldset [disabled]="busy()">
          <label>{{ text().hostnames }}<textarea name="editHosts" rows="2" [(ngModel)]="hosts" required></textarea></label>
          <label>{{ text().reason }}<input name="hostingReason" [(ngModel)]="reason" maxlength="500" required /></label>
          <div class="row">
            <button type="submit" class="btn-ghost" [disabled]="!reason.trim() || !hosts.trim()">{{ text().saveHosts }}</button>
            @if (company().state === 'Archived') {
              <button type="button" class="btn-primary" [disabled]="!reason.trim()" (click)="setArchived(false)">{{ text().restore }}</button>
            } @else {
              <button type="button" class="btn-danger" [disabled]="!reason.trim()" (click)="setArchived(true)">{{ text().archive }}</button>
            }
          </div>
        </fieldset>
      </form>
    }
  `,
  styles: [
    `
      :host { --pt-ink-2: var(--muted); }
      h3 {
        margin: 0 0 0.5rem;
        font-size: 0.78rem;
        font-weight: 600;
        letter-spacing: 0.06em;
        text-transform: uppercase;
        color: var(--pt-ink-2);
      }
      p { margin: 0 0 0.9rem; line-height: 1.6; max-width: 68ch; color: var(--pt-ink-2); }
      fieldset { border: 0; padding: 0; margin: 0; display: grid; gap: 0.9rem; }
      label { display: grid; gap: 0.35rem; font-size: 0.86rem; margin: 0; color: var(--text); }
      textarea, input { width: 100%; min-width: 0; }
      textarea { font: inherit; resize: vertical; }
      .row { display: flex; gap: 0.6rem; flex-wrap: wrap; }
    `,
  ],
})
export class CompanyHostingComponent {
  readonly i18n = inject(I18nService);
  private readonly api = inject(PlatformService);
  private readonly confirm = inject(ConfirmService);

  readonly company = input.required<PlatformCompany>();
  readonly changed = output<void>();
  readonly failed = output<unknown>();
  readonly busy = signal(false);
  readonly text = computed(() => platformText(this.i18n.lang()));

  hosts = '';
  reason = '';

  constructor() {
    effect(() => {
      this.hosts = this.company().hosts.join('\n');
      this.reason = '';
    });
  }

  async saveHosts(): Promise<void> {
    if (this.busy() || !this.reason.trim() || !this.hosts.trim()) return;
    if (!(await this.confirm.ask(saveRequest(this.i18n, this.company().id)))) return;
    await this.run(() =>
      this.api.updateHosts(this.company(), this.hosts.split(/[\s,;]+/).map((h) => h.trim()).filter(Boolean), this.reason.trim()));
  }

  async setArchived(archived: boolean): Promise<void> {
    if (archived) {
      const text = this.text();
      const yes = await this.confirm.ask({
        title: text.archiveTitle.replace('{name}', this.company().id),
        message: text.confirmArchive,
        confirmLabel: text.archive,
        danger: true,
      });
      if (!yes) return;
    }
    await this.run(() => this.api.setArchived(this.company(), archived, this.reason.trim()));
  }

  private async run(action: () => Promise<void>): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    try {
      await action();
      this.changed.emit();
    } catch (error) {
      this.failed.emit(error);
    } finally {
      this.busy.set(false);
    }
  }
}
