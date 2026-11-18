import { ApiError } from './api.service';

/**
 * The sentence a screen shows for a failed call, and the stable code of a refusal.
 *
 * <p>Its own small file so the start-up wiring in app.config.ts can reach it without pulling
 * `AdminService` — the whole back-office client — into the bundle every till loads first.
 * admin.service.ts re-exports these for the screens that already import them from there.</p>
 */

let translate: ((key: string) => string) | null = null;

/** Called once at start-up (app.config.ts) so the fixed sentences follow the reader's language. */
export function setErrorTranslator(t: ((key: string) => string) | null): void {
  translate = t;
}

export function adminErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 401) {
      return translate ? translate('error.sessionExpired') : 'Session expirée — reconnectez-vous.';
    }
    if (error.status === 0) {
      return translate ? translate('error.serverUnreachable') : 'Serveur injoignable.';
    }
    const code = problemCode(error);
    if (code === 'rateLimited') {
      const seconds = retryAfterSeconds(error) ?? 60;
      const pattern = translate ? translate('error.rateLimited') : 'Trop de requêtes. Réessayez dans {n} secondes.';
      return pattern.replace('{n}', String(seconds));
    }
    if (code === 'too_many_attempts') {
      const seconds = retryAfterSeconds(error) ?? 60;
      const pattern = translate ? translate('error.tooManyAttempts') : 'Trop de tentatives de connexion. Réessayez dans {n} secondes.';
      return pattern.replace('{n}', String(seconds));
    }
    if (code === 'busy') {
      return translate ? translate('error.busy') : 'Le serveur est occupé. Réessayez dans un instant.';
    }
    if (code === 'requestTooLarge') {
      return translate ? translate('error.requestTooLarge') : 'La demande est trop volumineuse.';
    }
    if (code === 'credentialsTooLong') {
      return translate ? translate('error.credentialsTooLong') : "L'identifiant ou le mot de passe est trop long.";
    }
    if (code === 'periodClosed') {
      // CLO-01: something dated inside closed books. The day is the server's `yyyy-MM-dd`, shown as dd/MM/yyyy.
      const day = closedThroughDay(error);
      const pattern = translate
        ? translate('error.periodClosed')
        : "Les comptes sont clôturés jusqu'au {date} : rien ne peut être daté de ce jour ou avant.";
      return pattern.replace('{date}', day);
    }
    if (code === 'untaxed_forbidden') {
      // E#2 (2026-11-02): a sale made without tax gives an untaxed invoice, which needs its permission.
      return translate
        ? translate('error.untaxedForbidden')
        : "Cette vente a été faite sans taxe : sa facture n'en porterait pas, et l'émettre demande l'autorisation d'émettre une facture sans taxe.";
    }
    if (code === 'rental_untaxed_forbidden') {
      // A rental contract without VAT (owner, 2026-11-02) needs the untaxed-invoice permission.
      return translate
        ? translate('error.rentalUntaxedForbidden')
        : "Un contrat hors taxe facture sans taxe : il demande l'autorisation d'émettre des factures sans taxe.";
    }
    if (code === 'confirm_own_forbidden') {
      return translate
        ? translate('error.confirmOwnForbidden')
        : 'Vous avez encaissé cette vente ; une autre personne confirme son paiement.';
    }
    return unwrap(error.message) || `HTTP ${error.status}`;
  }
  return (error as Error)?.message || (translate ? translate('error.unknown') : 'Unknown error');
}

/** The day a `periodClosed` refusal names (`yyyy-MM-dd`) as dd/MM/yyyy, or '' when it names none. */
function closedThroughDay(error: ApiError): string {
  try {
    const parsed = JSON.parse(error.message) as { closedThrough?: unknown };
    const iso = typeof parsed?.closedThrough === 'string' ? parsed.closedThrough : '';
    const [y, m, d] = iso.split('-');
    return y && m && d ? `${d}/${m}/${y}` : '';
  } catch {
    return '';
  }
}

/** The stable `code` of a ProblemDetails refusal; the transport hands the body back as the message. */
export function problemCode(error: unknown): string | null {
  if (!(error instanceof ApiError)) return null;
  try {
    const parsed = JSON.parse(error.message) as { code?: unknown };
    return typeof parsed?.code === 'string' ? parsed.code : null;
  } catch {
    return null;
  }
}

/** The seconds to wait before retrying, when a 429 or 503 refusal included one. */
export function retryAfterSeconds(error: unknown): number | null {
  if (!(error instanceof ApiError)) return null;
  try {
    const parsed = JSON.parse(error.message) as { retryAfterSeconds?: unknown };
    return typeof parsed?.retryAfterSeconds === 'number' && Number.isFinite(parsed.retryAfterSeconds)
      ? parsed.retryAfterSeconds
      : null;
  } catch {
    return null;
  }
}

/**
 * `Results.BadRequest("…")` serialises the sentence as a JSON string, so a refusal arrives
 * wearing quotation marks and every back-office tab printed them. Showing the wire format to
 * the person who mistyped something is not an explanation.
 */
function unwrap(message: string): string {
  const text = (message ?? '').trim();
  if (text.length > 1 && text.startsWith('"') && text.endsWith('"')) {
    try {
      const parsed: unknown = JSON.parse(text);
      if (typeof parsed === 'string') return parsed;
    } catch {
      /* not JSON after all — show it as it came */
    }
  }
  return text;
}
