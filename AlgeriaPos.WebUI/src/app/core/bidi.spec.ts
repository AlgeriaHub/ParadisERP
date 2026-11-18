import { isolate } from './bidi';

/**
 * The drawn codes (owner, 2026-10-05) — Cust-112610001, Comp-112610001, Emp-112610001 — are
 * Latin letters, one dash and digits, shown inside Arabic lines. These pin what the isolation is
 * for, measured in the browser rather than asserted about markup.
 */
describe('a drawn code inside an Arabic line', () => {
  let host: HTMLElement;

  beforeEach(() => {
    host = document.createElement('div');
    host.dir = 'rtl';
    host.lang = 'ar';
    host.style.cssText = 'position: absolute; inset-inline-start: 0; inline-size: 40rem; font: 16px monospace;';
    document.body.appendChild(host);
  });

  afterEach(() => host.remove());

  /** Where a run of text starts on screen, in pixels from the left. */
  function leftOf(needle: string): number {
    const walker = document.createTreeWalker(host, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const at = node.textContent!.indexOf(needle);
      if (at < 0) continue;
      const range = document.createRange();
      range.setStart(node, at);
      range.setEnd(node, at + needle.length);
      return range.getBoundingClientRect().left;
    }
    throw new Error(`"${needle}" is not on the line`);
  }

  it('wraps the code in a left-to-right isolate, and leaves nothing for no code', () => {
    expect(isolate('Emp-112610001')).toBe(String.fromCharCode(0x2066) + 'Emp-112610001' + String.fromCharCode(0x2069));
    expect(isolate(null)).toBe('');
    expect(isolate('')).toBe('');
  });

  it('left loose, lets the code pull the figures after it into its own direction', () => {
    // The reason for the rest: the period, said after the code, lands on its right.
    host.innerHTML = '<p>Emp-112610001 · 10/2026</p>';
    expect(leftOf('Emp')).toBeLessThan(leftOf('10/2026'));
  });

  it('isolated as text, keeps the code whole and the period after it, to its left', () => {
    host.innerHTML = `<p>${isolate('Emp-112610001')} · 10/2026</p>`;
    expect(leftOf('Emp')).toBeGreaterThan(leftOf('10/2026'));
    // The code itself still reads left to right: prefix, dash, figures.
    expect(leftOf('Emp')).toBeLessThan(leftOf('112610001'));
  });

  it('isolated by markup (bdi dir="ltr", or dir="ltr" on a span), does the same', () => {
    host.innerHTML = '<p><bdi dir="ltr">Cust-112610001</bdi> · 10/2026</p>';
    expect(leftOf('Cust')).toBeGreaterThan(leftOf('10/2026'));
    host.innerHTML = '<p><span dir="ltr">Comp-112610001</span> · 10/2026</p>';
    expect(leftOf('Comp')).toBeGreaterThan(leftOf('10/2026'));
    expect(leftOf('Comp')).toBeLessThan(leftOf('112610001'));
  });
});
