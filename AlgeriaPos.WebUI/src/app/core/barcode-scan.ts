/**
 * Telling a barcode scanner apart from a person typing.
 *
 * <p>A scanner is a keyboard. It sends the characters of the code and then Enter, and the
 * only thing that distinguishes it from a cashier at the same keyboard is speed: the gaps
 * between its characters are a few milliseconds, and a person's are tens of them. Every
 * screen that wants to accept a scan therefore has to make the same judgement, which is why
 * it is made once here.</p>
 *
 * <p>Why it is needed at all: the search box already reads a scan when it has focus, and on
 * a counter it very often does not — the cashier has just clicked a tile, set a quantity, or
 * come back from another tab. Scanning then types the code into nothing. The cashier scans
 * again, harder, and eventually reaches for the mouse. Listening at the document is what
 * makes the gun work wherever the screen happens to be.</p>
 */

/** The gap, in milliseconds, above which two characters came from a person's hands. */
const HUMAN_GAP_MS = 55;

/** Shorter than this is a keystroke, not an article. EAN-8 is the shortest real code. */
const MIN_CODE_LENGTH = 6;

export interface ScanBufferOptions {
  /** Longest gap between two characters of one scan. Raise it for a slow USB gun. */
  maxGapMs?: number;
  /** Shortest code to believe. */
  minLength?: number;
}

/**
 * The state of one possible scan in progress.
 *
 * <p>Separate from the DOM so the timing rule can be tested by handing it characters and
 * clock readings, which is the only way to test it that does not involve typing fast.</p>
 */
export class ScanBuffer {
  private chars: string[] = [];
  private lastAt = 0;
  private readonly maxGap: number;
  private readonly minLength: number;

  constructor(options: ScanBufferOptions = {}) {
    this.maxGap = options.maxGapMs ?? HUMAN_GAP_MS;
    this.minLength = options.minLength ?? MIN_CODE_LENGTH;
  }

  /**
   * Feed one keystroke. Returns the code when this keystroke completed a scan, else null.
   *
   * @param key the `KeyboardEvent.key`: one character, or 'Enter'.
   * @param at `event.timeStamp` — a clock that does not move when the page is busy, unlike
   *   Date.now() under a long render, which would read as a human pause and drop the scan.
   */
  push(key: string, at: number): string | null {
    if (key === 'Enter') {
      const code = this.chars.join('');
      this.reset();
      return code.length >= this.minLength ? code : null;
    }

    // A modifier, an arrow, a function key: not part of a code, and not a reason to throw
    // away one being read either — a gun sends nothing of the sort mid-code.
    if (key.length !== 1) return null;

    if (at - this.lastAt > this.maxGap) this.chars = [];
    this.chars.push(key);
    this.lastAt = at;
    return null;
  }

  reset(): void {
    this.chars = [];
    this.lastAt = 0;
  }

  /** How much of a possible code is in hand. Used to decide whether to swallow the Enter. */
  get length(): number {
    return this.chars.length;
  }
}

/** Whether the keystroke is already going somewhere that will read it. */
function isTyping(target: EventTarget | null): boolean {
  const element = target as HTMLElement | null;
  if (!element) return false;
  const tag = element.tagName;
  return (
    tag === 'INPUT' ||
    tag === 'TEXTAREA' ||
    tag === 'SELECT' ||
    element.isContentEditable === true
  );
}

/**
 * Listen for scans anywhere on the page. Returns the function that stops listening.
 *
 * <p>A keystroke that is already landing in a text box is left alone: that box is either the
 * search box, which reads the scan itself, or a quantity the cashier is in the middle of
 * typing, and stealing from either is worse than not listening at all.</p>
 */
export function listenForScans(
  handler: (code: string) => void,
  options: ScanBufferOptions = {},
): () => void {
  const buffer = new ScanBuffer(options);

  const onKeyDown = (event: KeyboardEvent) => {
    if (event.ctrlKey || event.altKey || event.metaKey) return;
    if (isTyping(event.target)) {
      buffer.reset();
      return;
    }

    const pending = buffer.length;
    const code = buffer.push(event.key, event.timeStamp);
    if (code) {
      // Only once a code is actually in hand. A bare Enter on a focused button has to stay a
      // press of that button.
      event.preventDefault();
      handler(code);
    } else if (event.key === 'Enter' && pending > 0) {
      event.preventDefault();
    }
  };

  document.addEventListener('keydown', onKeyDown, true);
  return () => document.removeEventListener('keydown', onKeyDown, true);
}
