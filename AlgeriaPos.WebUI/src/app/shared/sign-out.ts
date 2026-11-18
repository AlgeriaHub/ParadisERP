import { I18nService } from '../core/i18n.service';
import { ConfirmService } from '../ui/confirm.service';

/**
 * Asked before every sign-out the user starts (the owner, 2026-10-05): the item sits in the
 * same menu as the language and the back office, and one slip used to end the session.
 *
 * <p>Sales still waiting on this device are named, because the question a cashier has at that
 * moment is whether they are lost. They are not — they stay in the browser's store either
 * way — and the message says so rather than leaving a count with no meaning.</p>
 *
 * <p>An automatic sign-out (a user who removed their own account) does not ask: there is
 * nothing to choose.</p>
 */
export function confirmSignOut(confirm: ConfirmService, i18n: I18nService, pendingSales = 0): Promise<boolean> {
  const message = [
    i18n.t('nav.logout.confirmMessage'),
    pendingSales > 0 ? i18n.t('nav.logout.confirmPending', { n: pendingSales }) : '',
  ]
    .filter(Boolean)
    .join(' ');
  return confirm.ask({
    title: i18n.t('nav.logout.confirmTitle'),
    message,
    confirmLabel: i18n.t('nav.logout'),
  });
}
