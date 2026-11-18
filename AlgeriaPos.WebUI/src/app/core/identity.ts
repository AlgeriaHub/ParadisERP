/**
 * The identity parts that may stay empty but, filled in, must read as one (owner, 2026-10-02:
 * "if entered, correct — or empty"). The mirror of `IdDocumentTypes.IdentityProblem` on the
 * server, which refuses the same things in a sentence; this one names a translation key per field
 * so the form can say it under the box before anything is sent.
 */
export interface IdentityParts {
  idNumber?: string | null;
  idIssuer?: string | null;
  idIssuedOn?: string | null;
  birthDate?: string | null;
}

export type IdentityPart = keyof IdentityParts;

/** Nobody alive signs with a paper issued, or a birth, before this day. `IdDocumentTypes.Earliest`. */
export const EARLIEST_IDENTITY_DAY = '1900-01-01';

const NUMBER = /^(?=.*\d)[\p{L}\p{Nd}][\p{L}\p{Nd} /-]*$/u;
const LETTER = /\p{L}/u;

/**
 * One key per part that is filled in and wrong; an empty object when every part is empty or right.
 * Dates are the `yyyy-mm-dd` an `<input type="date">` holds, so they compare as strings.
 */
export function identityProblems(parts: IdentityParts, today: string): Partial<Record<IdentityPart, string>> {
  const problems: Partial<Record<IdentityPart, string>> = {};
  const number = (parts.idNumber ?? '').trim();
  if (number && !NUMBER.test(number)) problems.idNumber = 'idDoc.problem.number';
  const issuer = (parts.idIssuer ?? '').trim();
  if (issuer && !LETTER.test(issuer)) problems.idIssuer = 'idDoc.problem.issuer';
  const born = (parts.birthDate ?? '').slice(0, 10);
  if (born) {
    if (born > today) problems.birthDate = 'idDoc.problem.future';
    else if (born < EARLIEST_IDENTITY_DAY) problems.birthDate = 'idDoc.problem.tooOld';
  }
  const issued = (parts.idIssuedOn ?? '').slice(0, 10);
  if (issued) {
    if (issued > today) problems.idIssuedOn = 'idDoc.problem.future';
    else if (issued < EARLIEST_IDENTITY_DAY) problems.idIssuedOn = 'idDoc.problem.tooOld';
    else if (born && issued < born) problems.idIssuedOn = 'idDoc.problem.beforeBirth';
  }
  return problems;
}

/** Today as the reader's own calendar says it, in the `yyyy-mm-dd` form a date input holds. */
export function localToday(now: Date = new Date()): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}
