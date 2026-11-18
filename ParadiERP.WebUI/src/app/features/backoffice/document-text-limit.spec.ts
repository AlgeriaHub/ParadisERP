import { readableLimit } from './store-tab.component';

/**
 * The "changes per year" box, and the one way it goes wrong silently.
 *
 * <p>The limit is the owner's own setting (`store.docs.limit`), and 0 is a real value meaning
 * "frozen until he raises it again". That is exactly why an unreadable box must not coerce: an
 * admin who cleared the field to retype it was sending 0 and freezing the wording of every
 * printed document kind — the invoice, the credit note, the statement, the rental contract, both
 * notes and the payslip — behind a success message.</p>
 */
describe('the yearly wording allowance', () => {
  it('refuses a box that holds nothing rather than reading it as zero', () => {
    // Angular's number control writes null into the model when the field is emptied, and an
    // empty string arrives from a template-driven form that never coerced.
    expect(readableLimit(null)).toBe(false);
    expect(readableLimit(undefined)).toBe(false);
    expect(readableLimit('')).toBe(false);
  });

  it('accepts zero, because freezing the wording is a decision he is entitled to make', () => {
    expect(readableLimit(0)).toBe(true);
    expect(readableLimit('0')).toBe(true);
  });

  it('accepts a whole number and refuses what is not one', () => {
    expect(readableLimit(3)).toBe(true);
    expect(readableLimit(12)).toBe(true);
    expect(readableLimit(-1)).toBe(false);
    expect(readableLimit(2.5)).toBe(false);
    expect(readableLimit('trois')).toBe(false);
  });
});
