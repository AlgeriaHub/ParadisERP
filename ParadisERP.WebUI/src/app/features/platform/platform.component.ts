import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService, LANGS } from '../../core/i18n.service';
import { LanguageSwitchService } from '../../core/language-switch.service';
import { Lang, RegionalSettings } from '../../core/models';
import { AuditEntry, LanguagePackSummary, PlatformCompany, PlatformError, PlatformIdentity, PlatformService, PlatformUser, SubscriptionSettings } from '../../core/platform.service';
import { CompanyCreateComponent } from './company-create.component';
import { CompanyHostingComponent } from './company-hosting.component';
import { IntegrationsEditorComponent } from './integrations-editor.component';
import { LanguagePacksComponent } from './language-packs.component';
import { platformText } from './platform-text';
import { confirmSignOut } from '../../shared/sign-out';
import { ConfirmService } from '../../ui/confirm.service';
import { RegionalEditorComponent, RegionalSave } from './regional-editor.component';

@Component({
  selector: 'app-platform', standalone: true, imports: [FormsModule, RegionalEditorComponent, LanguagePacksComponent, CompanyCreateComponent, CompanyHostingComponent, IntegrationsEditorComponent],
  templateUrl: './platform.component.html', styleUrl: './platform.component.scss',
})
export class PlatformComponent implements OnInit {
  readonly i18n = inject(I18nService);
  private readonly switcher = inject(LanguageSwitchService);
  private readonly api = inject(PlatformService);
  private readonly confirm = inject(ConfirmService);
  readonly text = computed(() => platformText(this.i18n.lang()));
  readonly langs = LANGS;
  readonly name = signal<string | null>(null);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly saved = signal(false);
  readonly needsLogin = signal(false);
  readonly companies = signal<PlatformCompany[]>([]);
  readonly selected = signal<PlatformCompany | null>(null);
  readonly users = signal<PlatformUser[]>([]);
  readonly identities = signal<PlatformIdentity[]>([]);
  readonly audit = signal<AuditEntry[]>([]);
  readonly presets = signal<Record<string, RegionalSettings>>({});
  readonly packs = signal<LanguagePackSummary[]>([]);
  readonly packChoices = computed(() => this.packs().map(p => ({ code: p.code, label: p.label })));
  readonly presetCodes = computed(() => Object.keys(this.presets()));
  /** Shows the "new company" form in place of the company detail. */
  readonly creating = signal(false);
  readonly query = signal('');
  readonly filtered = computed(() => this.companies().filter(c =>
    (c.id + ' ' + c.hosts.join(' ')).toLowerCase().includes(this.query().toLowerCase())));
  readonly statuses = ['Trial', 'Active', 'Suspended', 'Cancelled', 'Unrestricted'];
  form: SubscriptionSettings | null = null;
  expiry = ''; reason = ''; subject = ''; userId = '';
  readonly operatorUsername = signal('admin');
  readonly operatorPassword = signal('');
  readonly loginError = signal('');
  readonly loggingIn = signal(false);

  async doLogin(): Promise<void> {
    if (!this.operatorPassword()) return;
    this.loggingIn.set(true);
    this.loginError.set('');
    try {
      const session = await this.api.login(this.operatorUsername(), this.operatorPassword());
      this.name.set(session.name);
      this.needsLogin.set(false);
      this.error.set('');
      this.operatorPassword.set('');
      await this.reload();
    } catch (err) {
      this.loginError.set(err instanceof Error ? err.message : 'فشل تسجيل الدخول');
    } finally {
      this.loggingIn.set(false);
    }
  }

  async ngOnInit(): Promise<void> {
    this.busy.set(true);
    try {
      const session = await this.api.session();
      this.name.set(session.name);
      const [companies, presets, packs] = await Promise.all([this.api.list(), this.api.presets(), this.api.languagePacks()]);
      this.companies.set(companies);
      this.presets.set(presets);
      this.packs.set(packs);
    } catch (error) { this.handle(error); }
    finally { this.busy.set(false); }
  }
  /**
   * The tone a company's state is drawn in, so the colour agrees with the word beside it.
   * Everything that is not archived used to be green, which told the operator that a company
   * three days from expiry was in the same condition as one on a paid year.
   */
  tone(company: PlatformCompany): 'ok' | 'warn' | 'danger' {
    if (!company.access.canOperate) return 'danger';
    return company.access.status === 'Active' || company.access.status === 'Unrestricted' ? 'ok' : 'warn';
  }

  onLangChange(lang: Lang, select?: HTMLSelectElement): void {
    if (select) select.value = this.i18n.lang();
    void this.switcher.request(lang);
  }

  signIn(): void { window.location.assign('/api/auth/sso/login'); }
  async logout(): Promise<void> {
    if (!(await confirmSignOut(this.confirm, this.i18n))) return;
    try { await this.api.logout(); this.name.set(null); this.needsLogin.set(true); this.selected.set(null); }
    catch (error) { this.handle(error); }
  }
  startCreate(): void { this.creating.set(true); this.selected.set(null); this.saved.set(false); this.error.set(''); }
  async companyCreated(id: string): Promise<void> {
    this.creating.set(false);
    await this.reload();
    const created = this.companies().find(c => c.id === id);
    if (created) await this.select(created);
  }
  async hostingChanged(): Promise<void> {
    this.saved.set(false);
    await this.reload();
    if (!this.error()) this.saved.set(true);
  }
  async select(company: PlatformCompany): Promise<void> {
    this.creating.set(false);
    this.selected.set(company); this.form = { ...company.settings };
    this.expiry = company.settings.expiresAtUtc ? new Date(company.settings.expiresAtUtc).toISOString().slice(0, 16) : '';
    this.reason = ''; this.subject = ''; this.userId = ''; this.saved.set(false); this.error.set('');
    this.busy.set(true); this.users.set([]); this.identities.set([]); this.audit.set([]);
    try {
      const [users, identities, audit] = await Promise.all([this.api.users(company.id), this.api.identities(company.id), this.api.audit(company.id)]);
      this.users.set(users); this.identities.set(identities); this.audit.set(audit);
      this.userId = users[0]?.id ?? '';
    } catch (error) { this.handle(error); }
    finally { this.busy.set(false); }
  }
  async reload(): Promise<void> {
    this.busy.set(true); this.error.set('');
    try {
      const [companies, packs] = await Promise.all([this.api.list(), this.api.languagePacks()]);
      this.companies.set(companies);
      this.packs.set(packs);
      const current = this.companies().find(c => c.id === this.selected()?.id);
      if (current) await this.select(current);
    } catch (error) { this.handle(error); }
    finally { this.busy.set(false); }
  }
  async save(): Promise<void> {
    const company = this.selected(); if (!company || !this.form || this.busy()) return;
    this.busy.set(true); this.error.set(''); this.saved.set(false);
    try {
      const settings = { ...this.form, expiresAtUtc: this.expiry ? new Date(this.expiry + 'Z').toISOString() : null };
      await this.api.save(company, settings, this.reason.trim());
      await this.reload(); this.saved.set(true);
    } catch (error) { this.handle(error); }
    finally { this.busy.set(false); }
  }
  async saveRegional(change: RegionalSave): Promise<void> {
    const company = this.selected(); if (!company || this.busy()) return;
    this.busy.set(true); this.error.set(''); this.saved.set(false);
    try {
      await this.api.saveRegional(company, change.settings, change.reason);
      await this.reload(); this.saved.set(true);
    } catch (error) { this.handle(error); }
    finally { this.busy.set(false); }
  }
  async packsChanged(): Promise<void> {
    this.saved.set(false);
    await this.reload();
    if (!this.error()) this.saved.set(true);
  }
  packFailed(error: unknown): void { this.saved.set(false); this.handle(error); }
  integrationsSaved(): void { this.error.set(''); this.saved.set(true); }
  async link(): Promise<void> {
    const company = this.selected(); if (!company || !this.subject.trim() || !this.userId) return;
    this.busy.set(true); this.error.set('');
    try { await this.api.link(company.id, this.subject.trim(), this.userId); await this.select(company); }
    catch (error) { this.handle(error); }
    finally { this.busy.set(false); }
  }
  async unlink(identity: PlatformIdentity): Promise<void> {
    const company = this.selected(); if (!company) return;
    if (!(await this.confirm.ask({ title: this.text().confirmUnlink, confirmLabel: this.i18n.t('ui.confirm.ok'), danger: true }))) return;
    this.busy.set(true); this.error.set('');
    try { await this.api.unlink(company.id, identity.id); await this.select(company); }
    catch (error) { this.handle(error); }
    finally { this.busy.set(false); }
  }
  username(id: string): string { return this.users().find(u => u.id === id)?.username ?? id; }
  auditReason(entry: AuditEntry): string {
    try { const data = JSON.parse(entry.detail); return data.reason ?? data.Subject ?? entry.action; } catch { return entry.action; }
  }
  private handle(error: unknown): void {
    if (error instanceof PlatformError && error.status === 401) { this.needsLogin.set(true); this.error.set(this.text().expiredSession); }
    else if (error instanceof PlatformError && error.status === 403) this.error.set(this.text().forbidden);
    else if (error instanceof PlatformError && error.status === 404) this.error.set(this.text().unavailable);
    else this.error.set((error as Error).message);
  }
}
