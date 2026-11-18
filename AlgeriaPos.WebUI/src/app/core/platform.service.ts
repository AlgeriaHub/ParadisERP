import { Injectable } from '@angular/core';
import { LanguagePack, RegionalSettings, SubscriptionAccess } from './models';

export interface SubscriptionSettings {
  plan: string; status: string; expiresAtUtc: string | null; graceDays: number;
  maxUsers: number | null; maxBranches: number | null; maxDevices: number | null;
}
export interface PlatformCompany {
  id: string; hosts: string[]; revision: number; settings: SubscriptionSettings; access: SubscriptionAccess;
  regional: RegionalSettings; regionalRevision: number;
  /** Configuration companies are read-only here; Portal ones can be re-hosted and archived. */
  source: 'Configuration' | 'Portal'; state: 'Active' | 'Archived'; companyRevision: number; createdAtUtc: string | null;
}
/** Providers are SQL Server (production) and SQLite (development/tests); a single-company deployment needs no third one. */
export interface NewCompany {
  id: string; hosts: string[]; databaseProvider: 'Sqlite' | 'SqlServer'; connectionString: string | null;
  country: string; adminUsername: string; subscription: SubscriptionSettings; reason: string;
}
/** The bootstrap password is only ever in this response. */
export interface CreatedCompany { id: string; adminUsername: string; bootstrapPassword: string; }
export interface LanguagePackSummary {
  code: string; label: string; rtl: boolean; fallbackLanguage: string; strings: number; revision: number;
}
export interface PlatformIdentity { id: string; subject: string; userId: string; }
export interface PlatformUser { id: string; username: string; fullName: string; role: string; isActive: boolean; }
export interface AuditEntry { id: string; actorSubject: string; action: string; detail: string; createdAtUtc: string; }
export interface CardTerminalSettings { mode: string; requireCardReference: boolean; }
export interface SmsGatewaySettings {
  enabled: boolean; url: string; method: string; tokenHeader: string; contentType: string; bodyTemplate: string;
}
export interface SmtpSettings {
  enabled: boolean; host: string; port: number; useTls: boolean; user: string; from: string;
}
export interface WebhookSettings { enabled: boolean; url: string; }
/** Non-secret, operator-owned integration settings of one company. */
export interface IntegrationSettings {
  card: CardTerminalSettings; sms: SmsGatewaySettings; email: SmtpSettings; webhook: WebhookSettings;
}
/** A secret change: a value replaces the stored one, `clear*` removes it, absent keeps it. */
export interface IntegrationSecretChanges {
  smsToken?: string; clearSmsToken?: boolean;
  smtpPassword?: string; clearSmtpPassword?: boolean;
  webhookSecret?: string; clearWebhookSecret?: boolean;
}
/** What the portal may see: settings, whether each secret is stored, and the delivery backlog. */
export interface CompanyIntegrations {
  settings: IntegrationSettings;
  revision: number;
  hasSmsToken: boolean;
  hasSmtpPassword: boolean;
  hasWebhookSecret: boolean;
  outbox: { pending: number; failing: number; lastError: string | null };
}
export class PlatformError extends Error { constructor(message: string, readonly status: number) { super(message); } }

@Injectable({ providedIn: 'root' })
export class PlatformService {
  private csrf = '';
  async session(): Promise<{ name: string }> {
    const session = await this.request<{ name: string; csrfToken: string }>('/session');
    this.csrf = session.csrfToken;
    return session;
  }
  list(): Promise<PlatformCompany[]> { return this.request('/companies'); }
  users(id: string): Promise<PlatformUser[]> { return this.request(`/companies/${encodeURIComponent(id)}/users`); }
  identities(id: string): Promise<PlatformIdentity[]> { return this.request(`/companies/${encodeURIComponent(id)}/identities`); }
  audit(id: string): Promise<AuditEntry[]> { return this.request(`/companies/${encodeURIComponent(id)}/audit`); }
  save(company: PlatformCompany, settings: SubscriptionSettings, reason: string): Promise<void> {
    return this.request(`/companies/${encodeURIComponent(company.id)}/subscription`, 'PUT', { settings, revision: company.revision, reason });
  }
  presets(): Promise<Record<string, RegionalSettings>> { return this.request('/regional-presets'); }
  saveRegional(company: PlatformCompany, settings: RegionalSettings, reason: string): Promise<void> {
    return this.request(`/companies/${encodeURIComponent(company.id)}/regional`, 'PUT', { settings, revision: company.regionalRevision, reason });
  }
  languagePacks(): Promise<LanguagePackSummary[]> { return this.request('/language-packs'); }
  languagePack(code: string): Promise<LanguagePack> { return this.request(`/language-packs/${encodeURIComponent(code)}`); }
  saveLanguagePack(pack: LanguagePack, revision: number, reason: string): Promise<void> {
    return this.request(`/language-packs/${encodeURIComponent(pack.code)}`, 'PUT', { pack, revision, reason });
  }
  deleteLanguagePack(code: string): Promise<void> {
    return this.request(`/language-packs/${encodeURIComponent(code)}`, 'DELETE');
  }
  /** Creating the database and running its migrations can take a while on a remote server. */
  createCompany(company: NewCompany): Promise<CreatedCompany> { return this.request('/companies', 'POST', company, 120000); }
  updateHosts(company: PlatformCompany, hosts: string[], reason: string): Promise<void> {
    return this.request(`/companies/${encodeURIComponent(company.id)}/hosts`, 'PUT', { hosts, revision: company.companyRevision, reason });
  }
  setArchived(company: PlatformCompany, archived: boolean, reason: string): Promise<void> {
    return this.request(`/companies/${encodeURIComponent(company.id)}/${archived ? 'archive' : 'restore'}`, 'POST', { revision: company.companyRevision, reason });
  }
  link(id: string, subject: string, userId: string): Promise<void> {
    return this.request(`/companies/${encodeURIComponent(id)}/identities`, 'POST', { subject, userId });
  }
  unlink(id: string, linkId: string): Promise<void> {
    return this.request(`/companies/${encodeURIComponent(id)}/identities/${encodeURIComponent(linkId)}`, 'DELETE');
  }
  integrations(id: string): Promise<CompanyIntegrations> {
    return this.request(`/companies/${encodeURIComponent(id)}/integrations`);
  }
  saveIntegrations(id: string, settings: IntegrationSettings, secrets: IntegrationSecretChanges, revision: number, reason: string): Promise<void> {
    return this.request(`/companies/${encodeURIComponent(id)}/integrations`, 'PUT', { settings, secrets, revision, reason });
  }
  /** Posts a signed test event; a gateway refusal comes back as ok:false, not an error. */
  testWebhook(id: string): Promise<{ ok: boolean; error?: string | null }> {
    return this.request(`/companies/${encodeURIComponent(id)}/integrations/test-webhook`, 'POST');
  }
  async logout(): Promise<void> {
    const response = await fetch('/api/auth/sso/logout', { method: 'POST', headers: { 'X-CSRF-TOKEN': this.csrf }, credentials: 'same-origin', signal: AbortSignal.timeout(10000) });
    if (!response.ok && response.status !== 401) throw new PlatformError('Sign-out failed. Try again.', response.status);
    this.csrf = '';
  }
  private async request<T>(path: string, method = 'GET', body?: unknown, timeoutMs = 30000): Promise<T> {
    const response = await fetch('/api/platform' + path, {
      method, credentials: 'same-origin', cache: 'no-store', signal: AbortSignal.timeout(timeoutMs),
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': this.csrf },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!response.ok) {
      const text = await response.text();
      let message = text;
      try { const data = JSON.parse(text); message = typeof data === 'string' ? data : data.detail ?? text; } catch {}
      throw new PlatformError(message || `HTTP ${response.status}`, response.status);
    }
    return response.status === 204 ? undefined as T : response.json();
  }
}
