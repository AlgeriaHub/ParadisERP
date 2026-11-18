import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService, MIN_PASSWORD_LENGTH } from '../../core/auth.service';
import { ConnectivityService } from '../../core/connectivity.service';
import { I18nService } from '../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';
import { landingPath } from '../../core/landing';
import { ConfirmService } from '../../ui/confirm.service';
import { actionRequest } from '../../ui/confirm-requests';
import { PasswordVisibilityDirective } from '../../ui/password-visibility.directive';

/** The three fields, by the name each one is visited under. */
type Field = 'current' | 'next' | 'confirm';

export const CHANGE_PASSWORD_PATH = '/change-password';

/**
 * The stable code inside a ProblemDetails body, when the server sent one. The sign-in path hands a
 * refusal back as the body's text (`AuthService.signIn`); anything that is not that shape is simply
 * not a code.
 */
function problemCode(text: string | undefined): string | null {
  if (!text) return null;
  try {
    const parsed = JSON.parse(text) as { code?: unknown };
    return typeof parsed?.code === 'string' ? parsed.code : null;
  } catch {
    return null;
  }
}

/**
 * Where to go back to: the address the menu passed, when it is one of this app's own and not this
 * screen or the sign-in; otherwise where the user lands after signing in. A `from` typed by hand
 * that points anywhere else is ignored rather than followed.
 */
export function returnPath(from: string | null | undefined, landing: string): string {
  if (!from || !from.startsWith('/') || from.startsWith('//')) return landing;
  if (from.startsWith(CHANGE_PASSWORD_PATH) || from.startsWith('/login')) return landing;
  return from;
}

/**
 * A signed-in user changes their own password (owner, 2026-10-05: "keep the password created from
 * admin side, and the user after login can change password").
 *
 * <p>Its own route, outside the back-office shell and the till, drawn as the sign-in card so it
 * reads as part of signing in. It is reached from the back office's user menu only — never from
 * the till's sale flow. The server is the authority on every rule; the three checks here only
 * save a round trip and say the problem beside its field.</p>
 *
 * <p>It needs the server. Offline it says so and sends nothing; the till's offline PIN unlock is a
 * different door and is not touched by any of this.</p>
 */
@Component({
  selector: 'app-change-password',
  standalone: true,
  imports: [FormsModule, PasswordVisibilityDirective],
  template: `
    <div class="login-wrap">
      <div class="login-card">
        <aside class="aside">
          <span class="logo">{{ i18n.countryCode() }}</span>
          <div class="aside-text">
            <strong>{{ i18n.t('app.title') }}</strong>
            @if (i18n.t('app.subtitle') !== i18n.t('app.title')) {
              <p>{{ i18n.t('app.subtitle') }}</p>
            }
          </div>
          <div class="aside-state">
            <span class="state" [class.state-ok]="connectivity.online()" [class.state-warn]="!connectivity.online()">
              {{ connectivity.online() ? i18n.t('status.online') : i18n.t('status.offline') }}
            </span>
          </div>
        </aside>

        <form class="panel" (ngSubmit)="submit()" novalidate>
          <div class="head">
            <div class="head-text">
              <h1>{{ i18n.t('account.password.title') }}</h1>
            </div>
          </div>
          <p class="muted hint">{{ i18n.t('account.password.intro', { name: name() }) }}</p>

          @if (done()) {
            <p class="notice ok" role="status" data-done>{{ i18n.t('account.password.done') }}</p>
            <button type="button" class="btn-primary submit" (click)="leave()">{{ i18n.t('account.password.continue') }}</button>
          } @else {
            @if (!connectivity.online()) {
              <p class="notice warn" role="status" data-offline>{{ i18n.t('account.password.offline') }}</p>
            }

            <!-- For the browser's password manager: which account the new password belongs to. -->
            <input type="text" name="username" autocomplete="username" [value]="signIn()" readonly hidden />

            <div class="field" [class.is-invalid]="problem('current')">
              <label for="cp-current">{{ i18n.t('account.password.current') }}</label>
              <input
                id="cp-current"
                name="currentPassword"
                type="password"
                autocomplete="current-password"
                [(ngModel)]="current"
                (blur)="visit('current')"
                [attr.aria-invalid]="problem('current') ? 'true' : null"
                [attr.aria-describedby]="problem('current') ? 'cp-current-error' : null"
              />
              @if (problem('current'); as p) {
                <small id="cp-current-error" class="field-error" role="alert">{{ p }}</small>
              }
            </div>

            <div class="field" [class.is-invalid]="problem('next')">
              <label for="cp-new">{{ i18n.t('account.password.new') }}</label>
              <input
                id="cp-new"
                name="newPassword"
                type="password"
                autocomplete="new-password"
                [(ngModel)]="next"
                (blur)="visit('next')"
                [attr.aria-invalid]="problem('next') ? 'true' : null"
                [attr.aria-describedby]="problem('next') ? 'cp-new-error' : 'cp-new-hint'"
              />
              @if (problem('next'); as p) {
                <small id="cp-new-error" class="field-error" role="alert">{{ p }}</small>
              } @else {
                <small id="cp-new-hint" class="muted">{{ i18n.t('account.password.newHint', { n: min }) }}</small>
              }
            </div>

            <div class="field" [class.is-invalid]="problem('confirm')">
              <label for="cp-confirm">{{ i18n.t('account.password.confirm') }}</label>
              <input
                id="cp-confirm"
                name="confirmPassword"
                type="password"
                autocomplete="new-password"
                [(ngModel)]="confirm"
                (blur)="visit('confirm')"
                [attr.aria-invalid]="problem('confirm') ? 'true' : null"
                [attr.aria-describedby]="problem('confirm') ? 'cp-confirm-error' : null"
              />
              @if (problem('confirm'); as p) {
                <small id="cp-confirm-error" class="field-error" role="alert">{{ p }}</small>
              }
            </div>

            @if (error()) {
              <p class="error" role="alert" data-error>{{ error() }}</p>
            }

            <div class="actions">
              <button type="button" class="btn-ghost" [disabled]="busy()" (click)="leave()">{{ i18n.t('common.cancel') }}</button>
              <button type="submit" class="btn-primary submit" [disabled]="busy()">
                {{ busy() ? i18n.t('common.loading') : i18n.t('account.password.submit') }}
              </button>
            </div>
          }
        </form>
      </div>
    </div>
  `,
  styleUrls: ['../login/login.component.scss'],
  styles: [
    `
      .field-error {
        color: var(--danger);
      }
      .field.is-invalid input {
        border-color: var(--danger);
      }
      .actions {
        display: flex;
        flex-wrap: wrap;
        justify-content: flex-end;
        align-items: center;
        gap: var(--sp-3);
        margin-block-start: var(--sp-1);
      }
      .actions .btn-ghost {
        min-height: var(--control-h);
      }
      .actions .submit {
        margin-block-start: 0;
      }
    `,
  ],
})
export class ChangePasswordComponent {
  readonly i18n = inject(I18nService);
  /** The screen's words travel with the back office's (`translations-account.ts`), registered before the first render. */
  private readonly words = this.i18n.extend(BACKOFFICE_TRANSLATIONS);
  readonly connectivity = inject(ConnectivityService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute, { optional: true });
  private readonly dialog = inject(ConfirmService);

  /** The same floor the users screen states when an administrator sets a password. */
  readonly min = MIN_PASSWORD_LENGTH;

  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly done = signal(false);
  readonly name = computed(() => this.auth.currentUser()?.fullName || this.auth.currentUser()?.username || '');
  readonly signIn = computed(() => this.auth.currentUser()?.username ?? '');

  current = '';
  next = '';
  confirm = '';

  /** Fields that have earned the right to complain: left once, or a save was tried. */
  private readonly visited = new Set<Field>();

  visit(field: Field): void {
    this.visited.add(field);
  }

  /**
   * What is wrong with a field, in words, once it may say so. A method, not a computed: the three
   * values are plain fields `[(ngModel)]` writes into, which no computed would ever see change.
   */
  problem(field: Field): string | null {
    if (!this.visited.has(field)) return null;
    const value = field === 'current' ? this.current : field === 'next' ? this.next : this.confirm;
    if (!value.trim()) return this.i18n.t('common.fieldRequired');
    if (field === 'next') {
      if (this.next.trim().length < this.min) return this.i18n.t('backoffice.users.passwordMin', { n: this.min });
      if (this.current && this.next === this.current) return this.i18n.t('account.password.error.same');
    }
    if (field === 'confirm' && this.confirm !== this.next) return this.i18n.t('account.password.error.mismatch');
    return null;
  }

  valid(): boolean {
    return (
      !!this.current.trim() &&
      this.next.trim().length >= this.min &&
      this.next !== this.current &&
      this.confirm === this.next
    );
  }

  async submit(): Promise<void> {
    if (this.busy()) return;
    this.visit('current');
    this.visit('next');
    this.visit('confirm');
    this.error.set(null);
    if (!this.valid()) return;

    if (!(await this.dialog.ask(actionRequest(this.i18n, this.i18n.t('account.password.submit'))))) return;

    this.busy.set(true);
    try {
      // The screen needs the server. The last probe may be stale either way, so a screen that
      // reads offline asks once more before refusing — and refuses without sending anything.
      const reachable = this.connectivity.online() || (await this.connectivity.probe());
      if (!reachable) {
        this.error.set(this.i18n.t('account.password.offline'));
        return;
      }
      const result = await this.auth.changePassword(this.current, this.next);
      if (!result.ok) {
        this.error.set(this.refusal(result.error));
        return;
      }
      // Saved, and the session in hand is now the fresh one the server answered. Nothing typed
      // is kept once it has done its job.
      this.current = this.next = this.confirm = '';
      this.visited.clear();
      this.done.set(true);
    } finally {
      this.busy.set(false);
    }
  }

  /** Back where the reader came from, or where they land after signing in. */
  leave(): void {
    const from = this.route?.snapshot.queryParamMap.get('from');
    void this.router.navigateByUrl(returnPath(from, landingPath(this.auth.currentUser())));
  }

  /** The server's refusal in the reader's language; it names the rule by a stable code. */
  private refusal(error: string | undefined): string {
    if (error === 'unreachable') return this.i18n.t('account.password.offline');
    // A 401: the session this was sent with is no longer one the server accepts.
    if (error === 'credentials') return this.i18n.t('account.password.session');
    switch (problemCode(error)) {
      case 'password_wrong':
        return this.i18n.t('account.password.error.wrong');
      case 'password_too_short':
        return this.i18n.t('backoffice.users.passwordMin', { n: this.min });
      case 'password_same':
        return this.i18n.t('account.password.error.same');
      // The current password is a guess like any sign-in, and the server caps them the same way.
      case 'too_many_attempts':
        return this.i18n.t('account.password.error.tooMany');
      default:
        return this.i18n.t('account.password.failed');
    }
  }
}
