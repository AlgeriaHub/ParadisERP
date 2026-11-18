import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { I18nService, LANGS } from '../../core/i18n.service';
import { LanguageSwitchService } from '../../core/language-switch.service';
import { Lang } from '../../core/models';

export interface TrialSignupResponse {
  slug: string;
  host: string;
  loginUrl: string;
  adminUsername: string;
  bootstrapPassword: string;
  expiresAtUtc: string;
}

@Component({
  selector: 'app-landing',
  standalone: true,
  imports: [RouterLink, FormsModule],
  templateUrl: './landing.component.html',
  styleUrl: './landing.component.scss',
})
export class LandingComponent {
  readonly i18n = inject(I18nService);
  readonly languages = LANGS;
  private readonly switcher = inject(LanguageSwitchService);
  private readonly api = inject(ApiService);

  readonly showTrialModal = signal(false);
  readonly loading = signal(false);
  readonly errorMessage = signal('');
  readonly trialResult = signal<TrialSignupResponse | null>(null);
  readonly copied = signal(false);

  storeName = '';
  slug = '';
  adminEmail = '';
  country = 'DZ';
  private slugManuallyEdited = false;

  readonly countries = [
    { code: 'DZ', label: 'Algeria (DZ)' },
    { code: 'SA', label: 'Saudi Arabia (SA)' },
    { code: 'MA', label: 'Morocco (MA)' },
    { code: 'TN', label: 'Tunisia (TN)' },
    { code: 'FR', label: 'France (FR)' },
  ];

  changeLanguage(lang: Lang): void {
    void this.switcher.request(lang);
  }

  openTrialModal(): void {
    this.errorMessage.set('');
    this.trialResult.set(null);
    this.copied.set(false);
    this.showTrialModal.set(true);
  }

  closeTrialModal(): void {
    this.showTrialModal.set(false);
  }

  onStoreNameInput(val: string): void {
    this.storeName = val;
    if (!this.slugManuallyEdited) {
      this.slug = this.slugify(val);
    }
  }

  onSlugInput(val: string): void {
    this.slugManuallyEdited = true;
    this.slug = this.slugify(val);
  }

  private slugify(text: string): string {
    return text
      .toLowerCase()
      .trim()
      .replace(/[^a-z0-9]/g, '-')
      .replace(/-+/g, '-')
      .replace(/^-|-$/g, '')
      .slice(0, 30);
  }

  async copyPassword(text: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(text);
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 2000);
    } catch {
      // clipboard fallback
    }
  }

  async submitTrial(): Promise<void> {
    if (!this.slug || this.slug.length < 3) {
      this.errorMessage.set(this.i18n.lang() === 'ar' ? 'يرجى إدخال نطاق فرعي صالح (3 أحرف على الأقل)' : 'Please enter a valid subdomain (at least 3 characters)');
      return;
    }

    this.loading.set(true);
    this.errorMessage.set('');

    try {
      const response = await this.api.postJson<TrialSignupResponse>('/api/trials', {
        slug: this.slug,
        companyName: this.storeName || this.slug,
        adminEmail: this.adminEmail || 'admin',
        country: this.country,
      });
      this.trialResult.set(response);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : 'Trial setup failed';
      this.errorMessage.set(msg);
    } finally {
      this.loading.set(false);
    }
  }
}
