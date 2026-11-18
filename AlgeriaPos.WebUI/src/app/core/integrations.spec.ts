import {
  MAX_CARD_REFERENCE_LENGTH,
  NO_INTEGRATIONS,
  missingCardReference,
  normalizeRecipient,
  paymentReference,
} from './integrations.service';
import { settleSplit } from './money';

describe('card references', () => {
  const required = { ...NO_INTEGRATIONS, cardMode: 'ExternalTerminal' as const, requireCardReference: true };

  it('are only required for card payments when the operator asks for them', () => {
    expect(missingCardReference(required, 'Card', 100, '')).toBeTrue();
    expect(missingCardReference(required, 'Card', 100, '  ')).toBeTrue();
    expect(missingCardReference(required, 'Card', 100, 'AUTH-1')).toBeFalse();
    expect(missingCardReference(required, 'Card', 0, '')).toBeFalse();
    expect(missingCardReference(required, 'Cash', 100, '')).toBeFalse();
    expect(missingCardReference(NO_INTEGRATIONS, 'Card', 100, '')).toBeFalse();
  });

  // Regression: the till required the cashier to type the approval number and then dropped it,
  // so a card sale reached the server with no reference at all and could not be reconciled.
  it('are kept on the card payment that carries them', () => {
    expect(paymentReference('Card', 'APPROVAL-99001')).toBe('APPROVAL-99001');
    expect(paymentReference('Card', '  APPROVAL-99001  ')).toBe('APPROVAL-99001');
    expect(paymentReference('Card', '')).toBeNull();
    expect(paymentReference('Card', '   ')).toBeNull();
    expect(paymentReference('Card', null)).toBeNull();
  });

  it('are capped to the width the server stores, so nothing is silently altered', () => {
    const long = 'A'.repeat(MAX_CARD_REFERENCE_LENGTH + 40);
    expect(paymentReference('Card', long)!.length).toBe(MAX_CARD_REFERENCE_LENGTH);
  });

  it('stay a gift card code for gift cards, and nothing for the other methods', () => {
    expect(paymentReference('GiftCard', ' gc-7788 ')).toBe('GC-7788');
    expect(paymentReference('Cash', 'AUTH-1')).toBeNull();
    expect(paymentReference('Credit', 'AUTH-1')).toBeNull();
    expect(paymentReference('Loyalty', 'AUTH-1')).toBeNull();
  });

  it('survive split settlement', () => {
    const settled = settleSplit(
      [
        { method: 'Card', amount: 60, reference: 'AUTH-9' },
        { method: 'Cash', amount: 50 },
      ],
      100,
    );
    // Reported as tendered: 50 in cash, with the 10 of change taken off the drawer by the
    // server rather than here. The approval number is what this test is about, and it rides
    // through untouched either way.
    expect(settled).toEqual({
      parts: [
        { method: 'Card', amount: 60, reference: 'AUTH-9' },
        { method: 'Cash', amount: 50 },
      ],
      paid: 110,
      change: 10,
    });
  });
});

describe('receipt recipients', () => {
  it('normalize phone numbers like the server', () => {
    expect(normalizeRecipient('sms', ' 06 12-34.56 78 ')).toBe('0612345678');
    expect(normalizeRecipient('sms', '+213 555 11 22 33')).toBe('+213555112233');
    expect(normalizeRecipient('sms', '12345')).toBeNull();
    expect(normalizeRecipient('sms', 'call me')).toBeNull();
  });

  it('accept plain e-mail addresses only', () => {
    expect(normalizeRecipient('email', ' client@example.dz ')).toBe('client@example.dz');
    expect(normalizeRecipient('email', 'client@localhost')).toBeNull();
    expect(normalizeRecipient('email', 'Name <client@example.dz>')).toBeNull();
  });
});
