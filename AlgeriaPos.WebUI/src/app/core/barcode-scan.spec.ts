import { ScanBuffer } from './barcode-scan';

/** Types a string into the buffer at a fixed pace, returning whatever it emitted. */
function type(buffer: ScanBuffer, text: string, gapMs: number, from = 1000): string | null {
  let at = from;
  let emitted: string | null = null;
  for (const char of text) {
    at += gapMs;
    emitted = buffer.push(char, at) ?? emitted;
  }
  return buffer.push('Enter', at + gapMs) ?? emitted;
}

describe('ScanBuffer', () => {
  it('reads a code the gun sent', () => {
    // A USB gun is 2–15 ms a character.
    expect(type(new ScanBuffer(), '6130001234567', 8)).toBe('6130001234567');
  });

  it('ignores the same code typed by a person', () => {
    // Nobody hits thirteen keys at 90 ms and means a scan; they are filling in a field, and
    // the Enter at the end belongs to whatever they are filling in.
    expect(type(new ScanBuffer(), '6130001234567', 90)).toBeNull();
  });

  it('keeps only what came after the pause', () => {
    // A cashier pressing keys and then scanning: the stray characters must not end up glued
    // to the front of the code.
    const buffer = new ScanBuffer();
    buffer.push('a', 1000);
    buffer.push('b', 1200);
    expect(type(buffer, '6130001234567', 8, 1400)).toBe('6130001234567');
  });

  it('refuses a burst too short to be an article', () => {
    expect(type(new ScanBuffer(), '12', 8)).toBeNull();
  });

  it('accepts a slow gun when told what it is', () => {
    expect(type(new ScanBuffer({ maxGapMs: 120 }), '6130001234567', 100)).toBe('6130001234567');
  });

  it('is not disturbed by a modifier arriving mid-code', () => {
    // A gun that sends the code in capitals sends Shift with them.
    const buffer = new ScanBuffer();
    let at = 1000;
    for (const char of 'ABC123X') {
      buffer.push('Shift', (at += 4));
      buffer.push(char, (at += 4));
    }
    expect(buffer.push('Enter', at + 4)).toBe('ABC123X');
  });

  it('forgets a half-read code once it has been dealt with', () => {
    const buffer = new ScanBuffer();
    expect(type(buffer, '6130001234567', 8)).toBe('6130001234567');
    expect(buffer.length).toBe(0);
    expect(buffer.push('Enter', 5000)).toBeNull();
  });
});
