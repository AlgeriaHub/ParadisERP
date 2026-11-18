/**
 * Number boxes in Latin digits, whatever the browser's language (owner, 2026-10-15; Batch 4).
 *
 * <p>Chromium draws an `<input type="number">` in the browser's own locale, and nothing in the
 * page changes that — not `lang`, not `-u-nu-latn`. On a browser set to Arabic (Saudi, Egyptian…)
 * a salary, a rate or a count of working days therefore read ٤٥٠٠٠ and ٢٢, even on the French
 * screen, while every figure the app prints is in Latin digits.</p>
 *
 * <p>Only in such a browser, every number box becomes a text box with a numeric keyboard
 * (`inputmode`) and is marked `data-latin-number`. What is typed is put in Latin digits with a dot
 * before anything reads it: Arabic-Indic digits, the Arabic decimal sign and a comma become `1.5`,
 * spaces and letters go. Angular's number accessor was chosen for the element when the template
 * compiled (`type="number"` there), so the model still receives a number. ↑ and ↓ still step the
 * value. A browser whose language already writes Latin digits keeps the native box untouched.</p>
 */

const MARK = 'data-latin-number';

/** Whether the browser's language writes its numbers in other digits than 0–9. */
export function writesNonLatinDigits(locale: string | undefined): boolean {
  if (!locale) return false;
  try {
    return /[^0-9]/.test(new Intl.NumberFormat(locale, { useGrouping: false }).format(1234567890));
  } catch {
    return false;
  }
}

/**
 * A typed number in Latin digits with a dot: Arabic-Indic and Persian digits mapped, `٫` and `,`
 * as the decimal sign, spaces and `٬` dropped, one sign, one decimal sign, nothing else.
 */
export function latinNumberText(text: string): string {
  const mapped = text
    .replace(/[٠-٩]/g, (d) => String(d.charCodeAt(0) - 0x0660))
    .replace(/[۰-۹]/g, (d) => String(d.charCodeAt(0) - 0x06f0))
    .replace(/[٫,]/g, '.')
    .replace(/[\s  ٬]/g, '');
  let out = '';
  let dot = false;
  for (const ch of mapped) {
    if (ch >= '0' && ch <= '9') out += ch;
    else if (ch === '.' && !dot) {
      out += ch;
      dot = true;
    } else if ((ch === '-' || ch === '−') && out.length === 0) out += '-';
  }
  return out;
}

/** The value one step up or down, kept within min and max. */
export function steppedValue(value: string, stepAttr: string | null, minAttr: string | null, maxAttr: string | null, up: boolean): string {
  const step = stepAttr && stepAttr !== 'any' && Number(stepAttr) > 0 ? Number(stepAttr) : 1;
  // As many decimals as the step or the value already has, whichever is more: 52000.5 + 1 is 52001.5.
  const decimals = Math.max((String(step).split('.')[1] ?? '').length, (value.split('.')[1] ?? '').length);
  let next = (Number.parseFloat(value) || 0) + (up ? step : -step);
  if (minAttr !== null && minAttr !== '' && Number.isFinite(Number(minAttr))) next = Math.max(next, Number(minAttr));
  if (maxAttr !== null && maxAttr !== '' && Number.isFinite(Number(maxAttr))) next = Math.min(next, Number(maxAttr));
  return next.toFixed(decimals);
}

/** One number box made a text box with a numeric keyboard. */
export function convertNumberInput(input: HTMLInputElement): void {
  if (input.hasAttribute(MARK) || input.type !== 'number') return;
  const step = input.getAttribute('step');
  const whole = step !== null && step !== 'any' && Number.isInteger(Number(step)) && Number(step) >= 1;
  input.type = 'text';
  input.setAttribute('inputmode', whole ? 'numeric' : 'decimal');
  input.setAttribute(MARK, '');
  if (!input.hasAttribute('autocomplete')) input.setAttribute('autocomplete', 'off');
  const normal = latinNumberText(input.value);
  if (normal !== input.value) input.value = normal;
}

function convertWithin(node: Node): void {
  if (!(node instanceof Element)) return;
  if (node instanceof HTMLInputElement) convertNumberInput(node);
  node.querySelectorAll?.('input[type="number"]').forEach((el) => convertNumberInput(el as HTMLInputElement));
}

/**
 * Converts every number box now and later, in a browser whose language writes non-Latin digits.
 * Returns a function that stops watching (for tests); does nothing elsewhere.
 */
export function installLatinNumberInputs(doc: Document, locale: string | undefined = doc.defaultView?.navigator.language): () => void {
  if (!writesNonLatinDigits(locale)) return () => {};
  const root = doc.documentElement;
  convertWithin(root);

  const observer = new MutationObserver((records) => {
    for (const record of records) record.addedNodes.forEach(convertWithin);
  });
  observer.observe(root, { childList: true, subtree: true });

  // Capture on the document runs before the element's own listeners, so Angular reads the
  // normalised text.
  const onInput = (event: Event): void => {
    const el = event.target;
    if (!(el instanceof HTMLInputElement) || !el.hasAttribute(MARK)) return;
    const normal = latinNumberText(el.value);
    if (normal === el.value) return;
    const caret = el.selectionStart ?? el.value.length;
    const kept = latinNumberText(el.value.slice(0, caret)).length;
    el.value = normal;
    el.setSelectionRange(kept, kept);
  };
  const onKey = (event: KeyboardEvent): void => {
    const el = event.target;
    if (!(el instanceof HTMLInputElement) || !el.hasAttribute(MARK) || el.readOnly || el.disabled) return;
    if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
    event.preventDefault();
    el.value = steppedValue(el.value, el.getAttribute('step'), el.getAttribute('min'), el.getAttribute('max'), event.key === 'ArrowUp');
    el.dispatchEvent(new Event('input', { bubbles: true }));
  };
  doc.addEventListener('input', onInput, true);
  doc.addEventListener('keydown', onKey, true);

  return () => {
    observer.disconnect();
    doc.removeEventListener('input', onInput, true);
    doc.removeEventListener('keydown', onKey, true);
  };
}
