/**
 * A customer's phone (owner, 2026-11-17: "phone must be saved as mobile format", with the
 * country code "if we use messages in the future"). The rules are the country profile's
 * (`RegionalSettings.phone`; Algeria: +213, a mobile starting 5, 6 or 7), never constants here.
 * Mirrors `ParadisERP.Application/Regional/PhoneNumbers.cs`: the server stores what `normalizePhone`
 * returns and refuses what it calls not a mobile (`phoneNotMobile`).
 */
export interface PhoneNumberRules {
  /** Digits only, e.g. "213". */
  callingCode: string;
  /** What people type before a national number, e.g. "0"; empty where there is none. */
  trunkPrefix: string;
  /** A mobile's national number, as a regular expression, e.g. `^[567][0-9]{8}$`. */
  mobilePattern: string;
  /** Grouping on screen and paper, e.g. [3, 2, 2, 2] → +213 658 41 77 30. */
  displayGroups: number[];
  /** A mobile as people type it at home, e.g. "0555123456". */
  example: string;
}

export interface PhoneReading {
  /** What the server stores: null for none, the international form when it reads as a mobile. */
  value: string | null;
  isMobile: boolean;
}

/** The stable code of the server's refusal. */
export const PHONE_NOT_MOBILE = 'phoneNotMobile';

function isSet(rules: PhoneNumberRules | null | undefined): rules is PhoneNumberRules {
  return !!rules && !!rules.callingCode && !!rules.mobilePattern;
}

function isMobile(national: string, rules: PhoneNumberRules): boolean {
  if (!national) return false;
  try {
    return new RegExp(rules.mobilePattern).test(national);
  } catch {
    return false;
  }
}

/** +2130658417730: a trunk 0 typed after the calling code is dropped too. */
function withoutTrunk(national: string, rules: PhoneNumberRules): string {
  const trunk = rules.trunkPrefix ?? '';
  return trunk && national.startsWith(trunk) && !isMobile(national, rules) && isMobile(national.slice(trunk.length), rules)
    ? national.slice(trunk.length)
    : national;
}

/**
 * 06.58.41.77.30, 0658417730, 00213658417730 and +213 658 41 77 30 all read +213658417730. A
 * number written with + and another country's code is taken as given (8–15 digits).
 */
export function normalizePhone(typed: string | null | undefined, rules: PhoneNumberRules | null | undefined): PhoneReading {
  const trimmed = (typed ?? '').trim();
  if (!trimmed) return { value: null, isMobile: true };
  if (!isSet(rules)) return { value: trimmed, isMobile: true };

  let compact = trimmed.replace(/[\s.\-()]/g, '');
  if (compact.startsWith('00')) compact = '+' + compact.slice(2);
  const plus = compact.startsWith('+');
  const digits = plus ? compact.slice(1) : compact;
  if (!/^[0-9]+$/.test(digits)) return { value: trimmed, isMobile: false };

  const code = rules.callingCode;
  const trunk = rules.trunkPrefix ?? '';
  let national: string;
  if (plus) {
    if (!digits.startsWith(code)) {
      return digits.length >= 8 && digits.length <= 15 ? { value: '+' + digits, isMobile: true } : { value: trimmed, isMobile: false };
    }
    national = withoutTrunk(digits.slice(code.length), rules);
  } else if (trunk && digits.startsWith(trunk)) {
    national = digits.slice(trunk.length);
  } else if (digits.startsWith(code) && isMobile(withoutTrunk(digits.slice(code.length), rules), rules)) {
    national = withoutTrunk(digits.slice(code.length), rules);
  } else {
    national = digits;
  }
  return isMobile(national, rules) ? { value: '+' + code + national, isMobile: true } : { value: trimmed, isMobile: false };
}

/** A stored phone a save would write otherwise: one from before the rule, left for a person to fix. */
export function phoneNeedsFixing(stored: string | null | undefined, rules: PhoneNumberRules | null | undefined): boolean {
  if (!stored?.trim() || !isSet(rules)) return false;
  const reading = normalizePhone(stored, rules);
  return !reading.isMobile || reading.value !== stored;
}

/**
 * How a stored phone is read on screen and paper: +213 658 41 77 30 for the country's own, the
 * stored form for another country's, and a phone from before the rule exactly as it was typed.
 */
export function formatPhone(stored: string | null | undefined, rules: PhoneNumberRules | null | undefined): string {
  const value = (stored ?? '').trim();
  if (!value || !isSet(rules)) return value;
  const prefix = '+' + rules.callingCode;
  if (!value.startsWith(prefix) || !/^\+[0-9]+$/.test(value)) return value;
  const national = value.slice(prefix.length);
  const groups = rules.displayGroups ?? [];
  if (!groups.length || groups.reduce((a, b) => a + b, 0) !== national.length) return value;
  const parts: string[] = [];
  let at = 0;
  for (const size of groups) {
    parts.push(national.slice(at, at + size));
    at += size;
  }
  return `${prefix} ${parts.join(' ')}`;
}

/** The profile's example, as typed at home and as stored: "0555123456" and "+213 555 12 34 56". */
export function phoneExample(rules: PhoneNumberRules | null | undefined): { local: string; international: string } | null {
  if (!isSet(rules) || !rules.example) return null;
  const reading = normalizePhone(rules.example, rules);
  return reading.isMobile && reading.value ? { local: rules.example, international: formatPhone(reading.value, rules) } : null;
}
