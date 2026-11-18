import { AdminCustomer, Representative } from '../../../core/admin.service';
import { IdentityField, SignatoryIdentity, signatoryIdentityMissing } from '../../../core/rentals';
import { RentalContract } from './rentals-api.service';

/**
 * Who signs a contract, worked out the way the server works it out.
 *
 * <p>A person signs <b>himself or through an agent</b> (owner, 2026-10-06, reversing answer 11 for
 * persons; the debt stays with the customer either way). Himself, the name is the customer's and his
 * identity is not asked on the contract; an agent's name and identity are typed on it. A company
 * signs through its representative — the current one, or one from its representative history —
 * chosen from a list, never typed. Only a customer whose kind is not set leaves the name as free
 * text, because there is nothing to derive it from.</p>
 *
 * <p>The identity (document type, number, issuer, issue date, birth date) comes from the same
 * record, and the server takes the record's value wherever it has one. So a field the record
 * holds is shown and not editable here ({@link fromRecord}); a field it lacks is typed on the
 * contract. The document and its number are required at the first issue note (the issuer and the
 * two dates are optional since 2026-10-02, checked when filled), which the issue-note screen says
 * before the server does ({@link identityMissing}).</p>
 *
 * <p>The contract and issue-note heads read the frozen snapshot rather than the customer, because
 * the representative may have changed since and the paper keeps whoever signed it.</p>
 */

/**
 * The identity fields the customer and representative screens add (another part of this change).
 * Declared here as optional so this file reads them whether or not those screens have landed.
 */
export type IdentifiedCustomer = AdminCustomer & {
  idDocumentType?: string | null;
  idDocumentIssuer?: string | null;
  idDocumentIssuedOn?: string | null;
};

export type IdentifiedRepresentative = Representative & {
  idDocumentType?: string | null;
  idDocumentIssuer?: string | null;
  idDocumentIssuedOn?: string | null;
};

export interface SignatoryPrefill {
  name: string;
  idNumber: string;
  idType: string;
  idIssuer: string;
  /** 'YYYY-MM-DD' or ''. */
  idIssuedOn: string;
  birthDate: string;
}

const NOBODY: SignatoryPrefill = { name: '', idNumber: '', idType: '', idIssuer: '', idIssuedOn: '', birthDate: '' };

/** How the signatory is chosen for this customer: himself, one of the company's representatives, or typed. */
export type SignatoryMode = 'person' | 'company' | 'free';

export function signatoryMode(customer: AdminCustomer | undefined): SignatoryMode | null {
  if (!customer) return null;
  if (customer.kind === 'Person') return 'person';
  if (customer.kind === 'Company') return 'company';
  return 'free';
}

const day = (value: string | null | undefined) => (value ?? '').slice(0, 10);

/**
 * What the form fills in — the server's own derivation, done early. For a company, the
 * representative chosen (the current one when none is given); a representative's row holds no
 * birth date, so that field is always the contract's own.
 */
export function signatoryPrefill(
  customer: IdentifiedCustomer | undefined,
  representative?: IdentifiedRepresentative | null,
): SignatoryPrefill {
  if (!customer) return NOBODY;
  switch (customer.kind) {
    case 'Person':
      return {
        name: customer.fullName,
        idNumber: customer.idDocumentNumber ?? '',
        idType: customer.idDocumentType ?? '',
        idIssuer: customer.idDocumentIssuer ?? '',
        idIssuedOn: day(customer.idDocumentIssuedOn),
        birthDate: day(customer.birthDate),
      };
    case 'Company': {
      const rep = (representative ?? customer.representative) as IdentifiedRepresentative | null;
      return rep
        ? {
            name: rep.fullName,
            idNumber: rep.idDocumentNumber ?? '',
            idType: rep.idDocumentType ?? '',
            idIssuer: rep.idDocumentIssuer ?? '',
            idIssuedOn: day(rep.idDocumentIssuedOn),
            birthDate: '',
          }
        : NOBODY;
    }
    default:
      // Kind not set: nothing to derive from, and guessing would put a name on a paper.
      return NOBODY;
  }
}

/**
 * The identity fields the customer's record (or the chosen representative's) already holds. The
 * server keeps the record's value for these whatever the contract says, so the form shows them
 * and does not let them be typed over; the rest are the contract's to fill.
 */
export function fromRecord(mode: SignatoryMode | null, prefill: SignatoryPrefill): Set<keyof SignatoryPrefill> {
  const locked = new Set<keyof SignatoryPrefill>();
  if (mode !== 'person' && mode !== 'company') return locked;
  if (prefill.name) locked.add('name');
  for (const key of ['idNumber', 'idType', 'idIssuer', 'idIssuedOn', 'birthDate'] as const) {
    if (prefill[key]) locked.add(key);
  }
  return locked;
}

/**
 * Who may sign for a company: its current representative first, then everyone who ever was, newest
 * first. One entry per name — the server matches the signatory by name.
 */
export function representativeChoices<T extends Representative>(history: T[]): T[] {
  const ordered = [...history].sort((a, b) => {
    if (!a.toUtc !== !b.toUtc) return a.toUtc ? 1 : -1;
    return b.fromUtc.localeCompare(a.fromUtc);
  });
  const seen = new Set<string>();
  return ordered.filter((r) => {
    const key = r.fullName.trim().toLocaleLowerCase();
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

export interface SignatoryHint {
  key: string;
  /** True when the sentence explains why nothing could be prefilled; the form shows a warn chip beside it. */
  warn: boolean;
}

/** The sentence under the signatory fields, per customer kind. Null when no customer is chosen. */
export function signatoryHintKey(customer: AdminCustomer | undefined): SignatoryHint | null {
  if (!customer) return null;
  switch (customer.kind) {
    case 'Person':
      return { key: 'rent.sign.hint.person', warn: false };
    case 'Company':
      return customer.representative
        ? { key: 'rent.sign.hint.company', warn: false }
        : { key: 'rent.sign.hint.companyNone', warn: true };
    default:
      return { key: 'rent.sign.hint.unset', warn: true };
  }
}

/**
 * - `signed`: a name is on the paper (whatever else is missing — see {@link identityMissing}).
 * - `missing`: blank on a draft raised under the rule — the first issue note will be refused.
 * - `predates`: blank on a contract raised before the rule — never refused, named for what it is.
 * - `unrecorded`: blank, required, but already locked. The rule bites once, at the first note,
 *   so nothing is refused any more; the head says the name was not entered, in words, and
 *   blocks nothing. Unreachable through these screens (the first note would have been refused);
 *   an API caller can produce it, and a fourth word is more honest than borrowing one of the others.
 */
export type SignatoryState = 'signed' | 'missing' | 'predates' | 'unrecorded';

export function signatoryState(
  contract: Pick<RentalContract, 'signatoryName' | 'signatoryRequired' | 'isLocked'>,
): SignatoryState {
  if ((contract.signatoryName ?? '').trim()) return 'signed';
  if (!contract.signatoryRequired) return 'predates';
  return contract.isLocked ? 'unrecorded' : 'missing';
}

/** The contract's identity snapshot, as the rule reads it. */
export function identityOf(contract: Partial<RentalContract>): SignatoryIdentity {
  return {
    idType: contract.signatoryIdType,
    idNumber: contract.signatoryIdNumber,
    idIssuer: contract.signatoryIdIssuer,
    idIssuedOn: contract.signatoryIdIssuedOn,
    birthDate: contract.signatoryBirthDate,
  };
}

/**
 * What the first issue note would be refused for, beside the name: the server's
 * `RentalRules.SignatoryIdentityMissing` asked before the server is.
 */
export function identityMissing(
  contract: Pick<RentalContract, 'isLocked' | 'signatoryRequired'> & Partial<RentalContract>,
): IdentityField[] {
  // A person who signs himself is not asked for his paper (owner, 2026-10-06); his agent is.
  if (signsHimself(contract)) return [];
  return signatoryIdentityMissing(contract.isLocked, contract.signatoryRequired, identityOf(contract));
}

/** A person customer signing his own contract, not through an agent. */
export function signsHimself(contract: Partial<Pick<RentalContract, 'customerKind' | 'signedByAgent'>>): boolean {
  return contract.customerKind === 'Person' && !contract.signedByAgent;
}

/**
 * The head line for a signed contract: "Signed by {name}" or, for a company, "… for the company",
 * because the name on the paper is not the debtor's and the reader should not have to guess whose
 * it is.
 */
export function signedByKey(contract: Pick<RentalContract, 'customerKind'> & Partial<Pick<RentalContract, 'signedByAgent'>>): string {
  if (contract.customerKind === 'Company') return 'rent.sign.signedByFor';
  return contract.signedByAgent ? 'rent.sign.signedByAgent' : 'rent.sign.signedBy';
}
