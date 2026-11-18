import { AdminCustomer, CustomerKind, IdDocumentType } from '../../core/admin.service';

/**
 * What the customers screen decides about a customer's kind before the request is sent.
 *
 * <p>These mirror `CustomerRules` on the server: the same reason codes, so a refusal a clerk
 * can meet is said on the screen first, in the reader's language, and the English server
 * sentence stays the fallback. Pure functions, because the rules are the same whether they
 * are read from the form or from a test.</p>
 */

export type KindProblem = 'kindRequired' | 'kindCannotRevert';

/** The kind in words. "Kind not set" is a state, never an option, which is why it has a key and no chip. */
export function kindLabelKey(kind: CustomerKind): string {
  switch (kind) {
    case 'Person':
      return 'cust.kind.person';
    case 'Company':
      return 'cust.kind.company';
    default:
      return 'cust.kind.unset';
  }
}

/**
 * Whether this save must carry a first representative. A company acts through one, and the
 * one decision the feature is about is taken when the kind is set — in the same save. Once a
 * company has a current representative the form never sends one again: a departure is a
 * change, which keeps the history, not an overwrite.
 */
export function needsRepresentative(kind: CustomerKind, editing: AdminCustomer | null): boolean {
  return kind === 'Company' && (editing === null || editing.representative === null);
}

/**
 * Whether saving with this kind ends the company's current representative. Switching to a
 * person keeps the history and stops showing it; switching back later is a new appointment,
 * not a resurrection, and the form says so beforehand.
 */
export function representativeEndsOnSave(kind: CustomerKind, editing: AdminCustomer | null): boolean {
  return kind === 'Person' && editing?.kind === 'Company' && editing.representative !== null;
}

/**
 * A new customer says what it is; an existing one never goes back to unspecified. An
 * unspecified row that predates the distinction may still be edited without deciding —
 * fixing a phone number must not force the question — and its warn chip simply stays.
 */
export function kindProblem(isNew: boolean, storedKind: CustomerKind, kind: CustomerKind): KindProblem | null {
  if (kind !== 'Unspecified') return null;
  if (isNew) return 'kindRequired';
  return storedKind === 'Unspecified' ? null : 'kindCannotRevert';
}

export type TakeoverDayProblem = 'fromTooEarly' | 'fromInFuture';

/**
 * Why a takeover day cannot be sent, or null — the two refusals of
 * `CustomerRules.RepresentativeProblem` a clerk can meet, said on the screen before the server
 * says them in English. `minDay` is the day the current representative started and `today`
 * is now, both in the company's zone (`minDay` undefined when there is nobody to start after).
 * Days are yyyy-mm-dd, so plain string order is date order.
 */
export function takeoverDayProblem(day: string, minDay: string | undefined, today: string): TakeoverDayProblem | null {
  if (!day) return null;
  if (minDay && day < minDay) return 'fromTooEarly';
  if (day > today) return 'fromInFuture';
  return null;
}

/**
 * The instant the new representative takes over. Today means now; an earlier day means its
 * start. The current one's own start is the floor: a representative appointed at 14:32 and
 * replaced "the same day" would otherwise be sent midnight, hours before they started, and the
 * server — which keeps the history strictly ordered — would refuse the very day they began.
 * Clamped, the old row closes the moment it opened, which is exactly what a same-day change is.
 */
export function takeoverInstant(day: string, today: string, currentFromUtc: string | null | undefined): string {
  const at = !day || day === today ? new Date().toISOString() : `${day}T00:00:00Z`;
  if (!currentFromUtc) return at;
  // The server writes an instant it read back from the database without a zone designator;
  // it is UTC all the same, and comparing it as local time would move the floor by the offset.
  const floor = /(Z|[+-]\d\d:?\d\d)$/.test(currentFromUtc) ? currentFromUtc : `${currentFromUtc}Z`;
  return Date.parse(floor) > Date.parse(at) ? floor : at;
}

/** The identity papers the contract accepts, in the order the select offers them. */
export const ID_DOCUMENT_TYPES: readonly IdDocumentType[] = ['nationalCard', 'drivingLicence', 'passport'];

/** The paper's name in the reader's language; "not said" for none. */
export function idDocumentTypeKey(type: IdDocumentType | null | undefined): string {
  return type ? `idDoc.type.${type}` : 'idDoc.type.none';
}

/**
 * What the select holds, read back as the request's value: one of the three keys, or null for
 * "not said". Anything else — a stale value from an older row — is sent as null rather than
 * refused by the server.
 */
export function idDocumentTypeOrNull(value: string | null | undefined): IdDocumentType | null {
  return ID_DOCUMENT_TYPES.includes(value as IdDocumentType) ? (value as IdDocumentType) : null;
}
