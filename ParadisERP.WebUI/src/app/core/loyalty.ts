import { Customer, GiftCard, SalePaymentPart, Store } from './models';
import { sha256Hex } from './sha256';

/** Mirrors ParadisERP.Application.Loyalty.LoyaltyRules on the server. */
export interface LoyaltyProgram {
  enabled: boolean;
  pointsPerUnit: number;
  pointValue: number;
  minRedeemPoints: number;
}

export function loyaltyProgram(store: Store | null | undefined): LoyaltyProgram {
  return {
    enabled: !!store?.loyaltyEnabled,
    pointsPerUnit: store?.loyaltyPointsPerUnit ?? 0,
    pointValue: store?.loyaltyPointValue ?? 0,
    minRedeemPoints: store?.loyaltyMinRedeemPoints ?? 0,
  };
}

const EPS = 1e-9;

/**
 * Points are earned on what was not paid with points or gift cards, rounded down. For a refund
 * (the points it takes back) only the part refunded as points is excluded.
 */
export function earnedPoints(program: LoyaltyProgram, total: number, payments: SalePaymentPart[], refund = false): number {
  if (!program.enabled || program.pointsPerUnit <= 0) return 0;
  const excluded = payments
    .filter((p) => p.method === 'Loyalty' || (!refund && p.method === 'GiftCard'))
    .reduce((s, p) => s + p.amount, 0);
  const eligible = Math.max(0, total - excluded);
  return Math.floor(eligible * program.pointsPerUnit + EPS);
}

/** Points consumed to pay an amount, rounded up. */
export function pointsForAmount(program: LoyaltyProgram, amount: number): number {
  if (program.pointValue <= 0 || amount <= 0) return 0;
  return Math.ceil(amount / program.pointValue - EPS);
}

export function maxRedeemValue(program: LoyaltyProgram, points: number): number {
  if (!program.enabled || program.pointValue <= 0 || points < Math.max(1, program.minRedeemPoints)) return 0;
  return points * program.pointValue;
}

export type TenderProblem =
  | 'giftCardUnknown'
  | 'giftCardInactive'
  | 'giftCardExpired'
  | 'giftCardInsufficient'
  | 'loyaltyNeedsCustomer'
  | 'loyaltyDisabled'
  | 'loyaltyInsufficient';

export function normalizeCardCode(code: string | null | undefined): string {
  return (code ?? '').trim().toUpperCase();
}

/** The hash a till finds a card by: the same as the server's GiftCardRules.CodeHash. */
export function cardCodeHash(code: string | null | undefined): string {
  return sha256Hex(normalizeCardCode(code));
}

/**
 * Checks the gift card and loyalty parts of a payment against the till's local replica.
 * The server never rejects a completed sale, so this is where obviously wrong cards stop.
 */
export function tenderProblem(
  payments: SalePaymentPart[],
  cards: GiftCard[],
  customer: Customer | null,
  program: LoyaltyProgram,
  now = new Date(),
): TenderProblem | null {
  const perCard = new Map<string, number>();
  for (const p of payments.filter((x) => x.method === 'GiftCard' && x.amount > 0)) {
    const code = normalizeCardCode(p.reference);
    perCard.set(code, (perCard.get(code) ?? 0) + p.amount);
  }
  for (const [code, amount] of perCard) {
    const card = cards.find((c) => c.codeHash === cardCodeHash(code));
    if (!card) return 'giftCardUnknown';
    if (!card.isActive) return 'giftCardInactive';
    if (card.expiresAtUtc && new Date(card.expiresAtUtc) <= now) return 'giftCardExpired';
    if (amount > card.balance + EPS) return 'giftCardInsufficient';
  }
  const loyalty = payments.filter((p) => p.method === 'Loyalty').reduce((s, p) => s + p.amount, 0);
  if (loyalty > 0) {
    if (!customer) return 'loyaltyNeedsCustomer';
    if (!program.enabled) return 'loyaltyDisabled';
    if (loyalty > maxRedeemValue(program, customer.loyaltyPoints ?? 0) + EPS) return 'loyaltyInsufficient';
  }
  return null;
}
