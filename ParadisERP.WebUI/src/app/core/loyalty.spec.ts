import { cardCodeHash, earnedPoints, LoyaltyProgram, maxRedeemValue, pointsForAmount, tenderProblem } from './loyalty';
import { Customer, GiftCard, SalePaymentPart } from './models';

const program: LoyaltyProgram = { enabled: true, pointsPerUnit: 0.1, pointValue: 0.5, minRedeemPoints: 20 };
const pay = (method: SalePaymentPart['method'], amount: number, reference: string | null = null): SalePaymentPart => ({
  id: crypto.randomUUID(),
  method,
  amount,
  reference,
});
const card = (over: Partial<GiftCard> = {}): GiftCard => ({
  // A till holds a hint and the code's hash, never the code (security review, 2026-10-10).
  id: 'g1', version: 1, code: '…CD', codeHash: cardCodeHash('ABCD'), initialAmount: 100, balance: 100, isActive: true, expiresAtUtc: null, customerId: null, ...over,
});
const customer = (points: number): Customer => ({
  id: 'c1', version: 1, code: 'C1', fullName: 'X', phone: null, address: null, taxId: null, email: null,
  birthDate: null, creditLimit: 0, balance: 0, loyaltyPoints: points,
});

describe('loyalty maths (mirrors the server)', () => {
  it('earns on the amount not paid with points or gift cards, rounded down', () => {
    expect(earnedPoints(program, 1000, [pay('Cash', 1000)])).toBe(100);
    expect(earnedPoints(program, 1000, [pay('Cash', 500), pay('Loyalty', 200), pay('GiftCard', 300, 'ABCD')])).toBe(50);
    expect(earnedPoints(program, 99.9, [pay('Card', 99.9)])).toBe(9);
    expect(earnedPoints({ ...program, enabled: false }, 1000, [])).toBe(0);
    expect(earnedPoints(program, 100, [pay('GiftCard', 100, 'ABCD')], true)).toBe(10);
    expect(earnedPoints(program, 100, [pay('Loyalty', 100)], true)).toBe(0);
  });

  it('rounds redeemed points up and respects the threshold', () => {
    expect(pointsForAmount(program, 10.2)).toBe(21);
    expect(pointsForAmount(program, 10)).toBe(20);
    expect(maxRedeemValue(program, 19)).toBe(0);
    expect(maxRedeemValue(program, 101)).toBe(50.5);
  });
});

describe('tender checks', () => {
  const now = new Date('2026-09-17T10:00:00Z');

  it('accepts a valid card and points', () => {
    expect(tenderProblem([pay('GiftCard', 60, ' abcd '), pay('Loyalty', 10)], [card()], customer(40), program, now)).toBeNull();
  });

  it('refuses unknown, inactive, expired and overspent cards', () => {
    expect(tenderProblem([pay('GiftCard', 10, 'NOPE')], [card()], null, program, now)).toBe('giftCardUnknown');
    expect(tenderProblem([pay('GiftCard', 10, 'ABCD')], [card({ isActive: false })], null, program, now)).toBe('giftCardInactive');
    expect(tenderProblem([pay('GiftCard', 10, 'ABCD')], [card({ expiresAtUtc: '2026-09-01T00:00:00Z' })], null, program, now)).toBe('giftCardExpired');
    expect(tenderProblem([pay('GiftCard', 60, 'ABCD'), pay('GiftCard', 60, 'ABCD')], [card()], null, program, now)).toBe('giftCardInsufficient');
  });

  it('refuses points without a customer, a programme or enough points', () => {
    expect(tenderProblem([pay('Loyalty', 5)], [], null, program, now)).toBe('loyaltyNeedsCustomer');
    expect(tenderProblem([pay('Loyalty', 5)], [], customer(100), { ...program, enabled: false }, now)).toBe('loyaltyDisabled');
    expect(tenderProblem([pay('Loyalty', 5)], [], customer(19), program, now)).toBe('loyaltyInsufficient');
    expect(tenderProblem([pay('Loyalty', 11)], [], customer(20), program, now)).toBe('loyaltyInsufficient');
  });
});

describe('gift card codes on a till', () => {
  it('finds the card by the hash of what the customer hands over, however it is typed', () => {
    expect(cardCodeHash(' abcd ')).toBe(cardCodeHash('ABCD'));
    expect(tenderProblem([pay('GiftCard', 10, 'abcd')], [card()], null, program)).toBeNull();
  });
});
