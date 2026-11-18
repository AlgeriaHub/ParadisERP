import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import qrcode from 'qrcode-generator';
import { AuthService } from '../../core/auth.service';
import { ConnectivityService } from '../../core/connectivity.service';
import { I18nService } from '../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';
import { landingPath } from '../../core/landing';
import { Profile, ProfileService, TwoFactorSetup, squarePhoto } from '../../core/profile.service';
import { RegionalService } from '../../core/regional.service';
import { ConfirmService } from '../../ui/confirm.service';
import { actionRequest } from '../../ui/confirm-requests';
import { PasswordVisibilityDirective } from '../../ui/password-visibility.directive';
import { StatusComponent } from '../../ui/status.component';
import { toneOf } from '../../ui/status-tones';
import { formatDay } from '../backoffice/backoffice-format';
import { CHANGE_PASSWORD_PATH, returnPath } from './change-password.component';

export const PROFILE_PATH = '/account';

/** What the two-step card is doing: at rest, asking the password for an action, showing the key, or the new codes. */
type TwoFactorStep = 'idle' | 'password' | 'scan' | 'codes';
type PasswordFor = 'setup' | 'disable' | 'codes';

/** The stable code inside a refusal's body, when the server sent one. */
function problemCode(error: unknown): string | null {
  try {
    const parsed = JSON.parse((error as Error)?.message ?? '') as { code?: unknown };
    return typeof parsed?.code === 'string' ? parsed.code : null;
  } catch {
    return null;
  }
}

/** The QR code of the setup's address, as one SVG path of dark modules (no markup injected). */
export function qrPath(text: string): { size: number; d: string } {
  const qr = qrcode(0, 'M');
  qr.addData(text);
  qr.make();
  const size = qr.getModuleCount();
  let d = '';
  for (let row = 0; row < size; row++) {
    for (let col = 0; col < size; col++) if (qr.isDark(row, col)) d += `M${col} ${row}h1v1h-1z`;
  }
  return { size, d };
}

/**
 * "My profile" (owner, 2026-11-11): who the account is — name, sign-in ID, role, branch and the HR
 * record it belongs to, read-only — its photo, which the user changes, and the account's security:
 * the password and two-step login, which each user turns on for themselves with an authenticator app.
 *
 * <p>Its own route outside the back-office shell and the till, like the change of password, so a
 * cashier reaches it as well as an administrator. It needs the server; offline it says so.</p>
 */
@Component({
  selector: 'app-profile',
  standalone: true,
  imports: [FormsModule, RouterLink, PasswordVisibilityDirective, StatusComponent],
  template: `
    <main class="profile">
      <div class="top">
        <button type="button" class="btn-ghost" (click)="leave()">
          <span class="ic-dir" aria-hidden="true">‹</span> {{ i18n.t('account.profile.back') }}
        </button>
        <span class="state" [class.state-ok]="connectivity.online()" [class.state-warn]="!connectivity.online()">
          {{ connectivity.online() ? i18n.t('status.online') : i18n.t('status.offline') }}
        </span>
      </div>

      @if (loadError()) {
        <p class="notice warn" role="alert" data-load-error>{{ loadError() }}</p>
      }

      @if (profile(); as p) {
        <section class="card who" aria-labelledby="profile-name">
          <div class="avatar" [class.has-photo]="!!photos.photo()">
            @if (photos.photo(); as src) {
              <img [src]="src" [alt]="i18n.t('account.profile.photoAlt', { name: p.fullName })" />
            } @else {
              <span aria-hidden="true" dir="auto">{{ initials() }}</span>
            }
          </div>
          <div class="who-text">
            <h1 id="profile-name"><bdi>{{ p.fullName || p.username }}</bdi></h1>
            <p class="muted">{{ roleLabel() }}@if (branchName()) { · {{ branchName() }}}</p>
            <div class="photo-actions">
              <input #photoFile type="file" accept="image/jpeg,image/png,image/webp" hidden (change)="pickPhoto($event)" />
              <button type="button" class="btn-ghost" [disabled]="photoBusy()" (click)="photoFile.click()" data-photo-pick>
                {{ p.photoVersion ? i18n.t('account.profile.photoChange') : i18n.t('account.profile.photoAdd') }}
              </button>
              @if (p.photoVersion) {
                <button type="button" class="btn-ghost" [disabled]="photoBusy()" (click)="removePhoto()">
                  {{ i18n.t('account.profile.photoRemove') }}
                </button>
              }
            </div>
            @if (photoError()) {
              <p class="error" role="alert">{{ photoError() }}</p>
            }
          </div>
        </section>

        <section class="card" aria-labelledby="profile-details">
          <h2 id="profile-details">{{ i18n.t('account.profile.details') }}</h2>
          <dl class="facts">
            <div><dt>{{ i18n.t('account.profile.loginId') }}</dt><dd class="mono"><bdi dir="ltr">{{ p.loginId || p.username }}</bdi></dd></div>
            <div><dt>{{ i18n.t('account.profile.role') }}</dt><dd>{{ roleLabel() }}</dd></div>
            <div><dt>{{ i18n.t('account.profile.branch') }}</dt><dd>{{ branchName() || '—' }}</dd></div>
            @if (p.employee; as e) {
              <div><dt>{{ i18n.t('account.profile.employeeNumber') }}</dt><dd class="mono"><bdi dir="ltr">{{ e.number }}</bdi></dd></div>
              <div><dt>{{ i18n.t('account.profile.jobTitle') }}</dt><dd><bdi>{{ e.jobTitle || '—' }}</bdi></dd></div>
              <div><dt>{{ i18n.t('account.profile.phone') }}</dt><dd class="mono"><bdi dir="ltr">{{ e.phone || '—' }}</bdi></dd></div>
              <div><dt>{{ i18n.t('account.profile.email') }}</dt><dd><bdi>{{ e.email || '—' }}</bdi></dd></div>
              <div><dt>{{ i18n.t('account.profile.hiredOn') }}</dt><dd>{{ day(e.hiredOn) }}</dd></div>
            }
          </dl>
          <p class="muted note">{{ i18n.t('account.profile.detailsNote') }}</p>
        </section>

        <section class="card" aria-labelledby="profile-password">
          <h2 id="profile-password">{{ i18n.t('account.password.title') }}</h2>
          <p class="muted">{{ i18n.t('account.profile.passwordLead') }}</p>
          <a class="btn-ghost" [routerLink]="changePasswordPath" [queryParams]="{ from: here }" data-change-password>
            {{ i18n.t('account.password.title') }}
          </a>
        </section>

        <section class="card" aria-labelledby="profile-2fa" data-two-factor>
          <div class="card-head">
            <h2 id="profile-2fa">{{ i18n.t('account.twoFactor.title') }}</h2>
            <app-status [tone]="tone()">
              {{ p.twoFactor.enabled ? i18n.t('account.twoFactor.on') : i18n.t('account.twoFactor.off') }}
            </app-status>
          </div>
          <p class="muted">{{ i18n.t('account.twoFactor.lead') }}</p>

          @switch (step()) {
            @case ('idle') {
              @if (p.twoFactor.enabled) {
                <p>{{ i18n.t('account.twoFactor.since', { date: day(p.twoFactor.enabledAtUtc) }) }}</p>
                <p [class.notice]="p.twoFactor.recoveryCodesLeft <= 2" [class.warn]="p.twoFactor.recoveryCodesLeft <= 2">
                  {{ i18n.t('account.twoFactor.codesLeft', { n: p.twoFactor.recoveryCodesLeft }) }}
                </p>
                <div class="actions">
                  <button type="button" class="btn-ghost" (click)="ask('codes')">{{ i18n.t('account.twoFactor.newCodes') }}</button>
                  <button type="button" class="btn-danger" (click)="ask('disable')">{{ i18n.t('account.twoFactor.turnOff') }}</button>
                </div>
              } @else {
                <div class="actions">
                  <button type="button" class="btn-primary" (click)="ask('setup')">{{ i18n.t('account.twoFactor.turnOn') }}</button>
                </div>
              }
            }
            @case ('password') {
              <form class="step" (ngSubmit)="confirmPassword()" novalidate>
                <p>{{ i18n.t('account.twoFactor.passwordFor.' + passwordFor()) }}</p>
                <input type="text" name="username" autocomplete="username" [value]="p.username" readonly hidden />
                <div class="field">
                  <label for="tf-password">{{ i18n.t('account.password.current') }}</label>
                  <input id="tf-password" name="password" type="password" autocomplete="current-password" [(ngModel)]="password" required />
                </div>
                @if (error()) {
                  <p class="error" role="alert" data-error>{{ error() }}</p>
                }
                <div class="actions">
                  <button type="button" class="btn-ghost" [disabled]="busy()" (click)="cancel()">{{ i18n.t('common.cancel') }}</button>
                  <button type="submit" [class]="passwordFor() === 'disable' ? 'btn-danger' : 'btn-primary'" [disabled]="busy() || !password">
                    {{ busy() ? i18n.t('common.loading') : i18n.t('account.twoFactor.continue') }}
                  </button>
                </div>
              </form>
            }
            @case ('scan') {
              @if (setup(); as s) {
                <form class="step" (ngSubmit)="enable()" novalidate>
                  <ol class="steps">
                    <li>{{ i18n.t('account.twoFactor.scan1') }}</li>
                    <li>{{ i18n.t('account.twoFactor.scan2') }}</li>
                    <li>{{ i18n.t('account.twoFactor.scan3') }}</li>
                  </ol>
                  <div class="scan">
                    @if (qr(); as q) {
                      <svg class="qr" role="img" [attr.aria-label]="i18n.t('account.twoFactor.qrLabel')"
                        [attr.viewBox]="'-4 -4 ' + (q.size + 8) + ' ' + (q.size + 8)" shape-rendering="crispEdges">
                        <rect x="-4" y="-4" [attr.width]="q.size + 8" [attr.height]="q.size + 8" class="qr-ground" />
                        <path [attr.d]="q.d" class="qr-ink" />
                      </svg>
                    }
                    <div class="key">
                      <span class="muted">{{ i18n.t('account.twoFactor.keyLabel') }}</span>
                      <code class="mono" data-secret><bdi dir="ltr">{{ s.secret }}</bdi></code>
                      <span class="muted">{{ i18n.t('account.twoFactor.keyAccount', { issuer: s.issuer, account: s.account }) }}</span>
                    </div>
                  </div>
                  <div class="field">
                    <label for="tf-code">{{ i18n.t('login.twoFactor.code') }}</label>
                    <input id="tf-code" name="code" class="mono code" inputmode="numeric" autocomplete="one-time-code"
                      maxlength="7" [(ngModel)]="code" required />
                  </div>
                  @if (error()) {
                    <p class="error" role="alert" data-error>{{ error() }}</p>
                  }
                  <div class="actions">
                    <button type="button" class="btn-ghost" [disabled]="busy()" (click)="cancel()">{{ i18n.t('common.cancel') }}</button>
                    <button type="submit" class="btn-primary" [disabled]="busy() || !code.trim()">
                      {{ busy() ? i18n.t('common.loading') : i18n.t('account.twoFactor.confirm') }}
                    </button>
                  </div>
                </form>
              }
            }
            @case ('codes') {
              <div class="step" data-recovery-codes>
                <p class="notice ok" role="status">{{ i18n.t(justEnabled() ? 'account.twoFactor.enabledDone' : 'account.twoFactor.codesDone') }}</p>
                <p>{{ i18n.t('account.twoFactor.codesLead') }}</p>
                <ol class="codes mono" dir="ltr">
                  @for (c of codes(); track c) {
                    <li>{{ c }}</li>
                  }
                </ol>
                <div class="actions">
                  <button type="button" class="btn-ghost" (click)="copyCodes()">
                    {{ copied() ? i18n.t('account.twoFactor.copied') : i18n.t('account.twoFactor.copy') }}
                  </button>
                  <button type="button" class="btn-primary" (click)="doneWithCodes()">{{ i18n.t('account.twoFactor.saved') }}</button>
                </div>
              </div>
            }
          }
        </section>
      } @else if (!loadError()) {
        <p class="muted" role="status">{{ i18n.t('common.loading') }}</p>
      }
    </main>
  `,
  styles: [
    `
      :host {
        display: block;
        min-block-size: 100dvh;
        background: var(--bg);
        color: var(--text);
      }
      .profile {
        max-inline-size: var(--form-max);
        margin-inline: auto;
        padding: var(--sp-5) var(--sp-4) var(--sp-7);
        display: grid;
        gap: var(--sp-4);
      }
      .top {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: var(--sp-3);
      }
      .card {
        background: var(--surface);
        border: var(--line) solid var(--border);
        border-radius: var(--radius-card);
        box-shadow: var(--shadow-sm);
        padding: var(--sp-5);
        display: grid;
        gap: var(--sp-3);
        min-inline-size: 0;
      }
      .card h2 {
        margin: 0;
        font-size: var(--fs-md);
      }
      .card p {
        margin: 0;
      }
      .card-head {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: var(--sp-3);
        flex-wrap: wrap;
      }
      .who {
        grid-template-columns: auto 1fr;
        align-items: center;
        gap: var(--sp-5);
      }
      .who h1 {
        margin: 0;
        font-family: var(--font-display);
        font-size: var(--fs-page);
      }
      .who-text {
        display: grid;
        gap: var(--sp-2);
        min-inline-size: 0;
      }
      .avatar {
        inline-size: 6rem;
        block-size: 6rem;
        border-radius: 50%;
        overflow: hidden;
        display: grid;
        place-items: center;
        background: var(--primary);
        color: var(--on-primary);
        font-size: var(--fs-xl);
        font-weight: 700;
        border: var(--line) solid var(--border);
      }
      .avatar img {
        inline-size: 100%;
        block-size: 100%;
        object-fit: cover;
      }
      .photo-actions,
      .actions {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-2);
        align-items: center;
      }
      .actions {
        justify-content: flex-end;
      }
      .card > a.btn-ghost {
        justify-self: start;
      }
      .facts {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr));
        gap: var(--sp-3) var(--sp-5);
        margin: 0;
      }
      .facts div {
        display: grid;
        gap: 2px;
        min-inline-size: 0;
      }
      .facts dt {
        font-size: var(--fs-sm);
        color: var(--muted);
      }
      .facts dd {
        margin: 0;
        overflow-wrap: anywhere;
        text-align: start;
      }
      .note {
        font-size: var(--fs-sm);
      }
      .step {
        display: grid;
        gap: var(--sp-3);
      }
      .field {
        display: grid;
        gap: var(--sp-1);
        max-inline-size: 22rem;
      }
      .steps {
        margin: 0;
        padding-inline-start: var(--sp-5);
        display: grid;
        gap: var(--sp-1);
      }
      .scan {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-5);
        align-items: center;
      }
      .qr {
        inline-size: 11rem;
        block-size: 11rem;
        border-radius: var(--radius-sm);
        border: var(--line) solid var(--border);
      }
      /* A QR code, like paper, stays dark on white in every theme: phones read it that way. */
      .qr-ground {
        fill: #ffffff;
      }
      .qr-ink {
        fill: #000000;
      }
      .key {
        display: grid;
        gap: var(--sp-1);
        min-inline-size: 0;
      }
      .key code {
        font-size: var(--fs-md);
        letter-spacing: 0.05em;
        overflow-wrap: anywhere;
        user-select: all;
      }
      .code {
        direction: ltr;
        letter-spacing: 0.3em;
        font-size: var(--fs-lg);
      }
      .codes {
        margin: 0;
        padding: var(--sp-3) var(--sp-5);
        background: var(--surface-2);
        border: var(--line) solid var(--border);
        border-radius: var(--radius);
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(9rem, 1fr));
        gap: var(--sp-2) var(--sp-5);
        list-style-position: inside;
      }
      .error {
        color: var(--danger);
        margin: 0;
      }
      @media (max-width: 640px) {
        .who {
          grid-template-columns: 1fr;
          justify-items: start;
        }
        .card {
          padding: var(--sp-4);
        }
      }
    `,
  ],
})
export class ProfileComponent implements OnInit {
  readonly i18n = inject(I18nService);
  /** The screen's words travel with the back office's (`translations-account.ts`), registered before the first render. */
  private readonly words = this.i18n.extend(BACKOFFICE_TRANSLATIONS);
  readonly connectivity = inject(ConnectivityService);
  readonly photos = inject(ProfileService);
  private readonly auth = inject(AuthService);
  private readonly regional = inject(RegionalService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute, { optional: true });
  private readonly dialog = inject(ConfirmService);

  readonly changePasswordPath = CHANGE_PASSWORD_PATH;
  readonly here = PROFILE_PATH;

  readonly profile = signal<Profile | null>(null);
  readonly loadError = signal<string | null>(null);
  readonly photoBusy = signal(false);
  readonly photoError = signal<string | null>(null);

  readonly step = signal<TwoFactorStep>('idle');
  readonly passwordFor = signal<PasswordFor>('setup');
  readonly setup = signal<TwoFactorSetup | null>(null);
  readonly codes = signal<string[]>([]);
  readonly justEnabled = signal(false);
  readonly copied = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  password = '';
  code = '';

  readonly qr = computed(() => {
    const s = this.setup();
    return s ? qrPath(s.provisioningUri) : null;
  });
  readonly tone = computed(() => toneOf('twoFactor', this.profile()?.twoFactor.enabled ? 'on' : 'off'));

  readonly roleLabel = computed(() => {
    const p = this.profile();
    if (!p) return '';
    // A role the company created is shown by its own name; the three built-in ones in the reader's language.
    const builtin = this.i18n.t('nav.role.' + p.role.toLowerCase());
    return p.roleName && !['admin', 'manager', 'cashier'].includes(p.roleName.toLowerCase()) ? p.roleName : builtin;
  });

  readonly branchName = computed(() => {
    const p = this.profile();
    if (!p) return '';
    const byLang = { ar: p.branchNameAr, fr: p.branchNameFr, en: p.branchNameEn } as Record<string, string | null>;
    return byLang[this.i18n.lang()] || p.branchNameFr || p.branchNameAr || p.branchNameEn || '';
  });

  readonly initials = computed(() => {
    const name = (this.profile()?.fullName ?? '').trim();
    const parts = name.split(/\s+/).filter(Boolean);
    if (parts.length === 0) return '';
    if (parts.length === 1) return Array.from(parts[0])[0].toUpperCase();
    return (Array.from(parts[0])[0] + Array.from(parts[parts.length - 1])[0]).toUpperCase();
  });

  async ngOnInit(): Promise<void> {
    await this.reload();
  }

  private async reload(): Promise<void> {
    try {
      const profile = await this.photos.load();
      this.profile.set(profile);
      this.loadError.set(null);
      await this.photos.showPhoto(profile.photoVersion);
    } catch (error) {
      this.loadError.set(
        (error as { status?: number })?.status === 0 ? this.i18n.t('account.profile.offline') : this.i18n.t('account.profile.loadFailed'),
      );
    }
  }

  day(value: string | null | undefined): string {
    return value ? formatDay(value, this.i18n.lang(), this.regional.settings().timeZone) : '—';
  }

  // ---------- photo ----------

  async pickPhoto(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    this.photoError.set(null);
    this.photoBusy.set(true);
    try {
      let shrunk: Blob;
      try {
        shrunk = await squarePhoto(file);
      } catch {
        this.photoError.set(this.i18n.t('account.profile.photoInvalid'));
        return;
      }
      const version = await this.photos.uploadPhoto(shrunk);
      this.profile.update((p) => (p ? { ...p, photoVersion: version } : p));
    } catch (error) {
      this.photoError.set(this.refusal(error, 'account.profile.photoFailed'));
    } finally {
      this.photoBusy.set(false);
    }
  }

  async removePhoto(): Promise<void> {
    if (!(await this.dialog.ask(actionRequest(this.i18n, this.i18n.t('account.profile.photoRemove'))))) return;
    this.photoError.set(null);
    this.photoBusy.set(true);
    try {
      await this.photos.removePhoto();
      this.profile.update((p) => (p ? { ...p, photoVersion: null } : p));
    } catch (error) {
      this.photoError.set(this.refusal(error, 'account.profile.photoFailed'));
    } finally {
      this.photoBusy.set(false);
    }
  }

  // ---------- two-step login ----------

  ask(purpose: PasswordFor): void {
    this.passwordFor.set(purpose);
    this.password = '';
    this.error.set(null);
    this.step.set('password');
    setTimeout(() => document.getElementById('tf-password')?.focus());
  }

  cancel(): void {
    this.password = this.code = '';
    this.setup.set(null);
    this.error.set(null);
    this.step.set('idle');
  }

  async confirmPassword(): Promise<void> {
    if (this.busy() || !this.password) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const purpose = this.passwordFor();
      if (purpose === 'setup') {
        this.setup.set(await this.photos.setup(this.password));
        this.code = '';
        this.step.set('scan');
        setTimeout(() => document.getElementById('tf-code')?.focus());
      } else if (purpose === 'codes') {
        this.showCodes((await this.photos.newRecoveryCodes(this.password)).codes, false);
      } else {
        const status = await this.photos.disable(this.password);
        this.profile.update((p) => (p ? { ...p, twoFactor: status } : p));
        this.step.set('idle');
      }
      this.password = '';
    } catch (error) {
      this.error.set(this.refusal(error, 'account.twoFactor.failed'));
    } finally {
      this.busy.set(false);
    }
  }

  async enable(): Promise<void> {
    if (this.busy() || !this.code.trim()) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const { codes } = await this.photos.enable(this.code.trim());
      this.setup.set(null);
      this.code = '';
      this.showCodes(codes, true);
      this.profile.update((p) =>
        p ? { ...p, twoFactor: { enabled: true, enabledAtUtc: new Date().toISOString(), recoveryCodesLeft: codes.length } } : p,
      );
    } catch (error) {
      this.code = '';
      this.error.set(this.refusal(error, 'account.twoFactor.failed'));
    } finally {
      this.busy.set(false);
    }
  }

  private showCodes(codes: string[], enabled: boolean): void {
    this.codes.set(codes);
    this.justEnabled.set(enabled);
    this.copied.set(false);
    this.step.set('codes');
    if (!enabled) this.profile.update((p) => (p ? { ...p, twoFactor: { ...p.twoFactor, recoveryCodesLeft: codes.length } } : p));
  }

  async copyCodes(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.codes().join('\n'));
      this.copied.set(true);
    } catch {
      // No clipboard (plain http on a LAN address): the codes stay on screen to write down.
    }
  }

  /** The codes are shown once: leaving this step forgets them here too. */
  doneWithCodes(): void {
    this.codes.set([]);
    this.step.set('idle');
    void this.reload();
  }

  leave(): void {
    const from = this.route?.snapshot.queryParamMap.get('from');
    const back = returnPath(from, landingPath(this.auth.currentUser()));
    void this.router.navigateByUrl(back.startsWith(PROFILE_PATH) ? landingPath(this.auth.currentUser()) : back);
  }

  /** The server's refusal in the reader's language; it names the rule by a stable code. */
  private refusal(error: unknown, fallback: string): string {
    const status = (error as { status?: number })?.status;
    if (status === 0) return this.i18n.t('account.profile.offline');
    if (status === 401) return this.i18n.t('account.password.session');
    switch (problemCode(error)) {
      case 'password_wrong':
        return this.i18n.t('account.password.error.wrong');
      case 'too_many_attempts':
        return this.i18n.t('account.password.error.tooMany');
      case 'two_factor_wrong':
        return this.i18n.t('login.twoFactor.wrong');
      case 'two_factor_no_setup':
        return this.i18n.t('account.twoFactor.noSetup');
      case 'photo_invalid':
        return this.i18n.t('account.profile.photoInvalid');
      case 'photo_too_large':
        return this.i18n.t('account.profile.photoTooLarge');
      default:
        return this.i18n.t(fallback);
    }
  }
}
