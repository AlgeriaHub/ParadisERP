/** Unicode's left-to-right isolate and its closing mark, spelled as code points so the source shows them. */
const LRI = String.fromCharCode(0x2066);
const PDI = String.fromCharCode(0x2069);

/**
 * A Latin code set inside text that may be Arabic — a sentence, an `<option>`, a joined label —
 * as its own left-to-right island (LRI … PDI), for the places an element cannot go.
 *
 * <p>A drawn code such as Cust-112610001 or Emp-112610001 holds together by itself, but its
 * digits are "weak": left loose, the Unicode algorithm lets the code's Latin letters claim the
 * numbers that follow it too, so "Emp-112610001 · 10/2026" in an Arabic line reads as one
 * left-to-right run with the period on the wrong side. Isolated, the code is one unit and the
 * line around it keeps the reader's direction. In a template, `dir="ltr"` on the element (or a
 * `<bdi dir="ltr">`) does the same.</p>
 */
export function isolate(text: string | null | undefined): string {
  return text ? `${LRI}${text}${PDI}` : '';
}
