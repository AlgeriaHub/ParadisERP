/**
 * Finding a product the way somebody at a counter does: by scanning it, by typing a few
 * letters of its name, or by typing the start of its code.
 *
 * <p>This lives apart from any screen because the same question is asked in five places —
 * the till, a rental contract, an issue note, a stock receipt, a transfer — and each of them
 * had written its own `includes()` against a different set of fields. One of them searched
 * the barcode and the others did not, which is the sort of difference nobody reports as a
 * bug; they just stop using the box.</p>
 *
 * <p>Nothing here touches Angular, so the matching rules are testable on their own, which is
 * where the Arabic folding below is actually verified.</p>
 */

/** As little of a product as searching needs, so both the till's and the admin's fit. */
export interface SearchableProduct {
  id: string;
  sku: string;
  barcode?: string | null;
  nameAr: string;
  nameFr: string;
  nameEn: string;
}

// Harakat, the dagger alef and the tatweel. Also ٔ/ٕ, which is what NFD leaves
// behind when it takes أ apart, so folding has to run after the decomposition below.
const ARABIC_MARKS = /[ً-ٰٕـ]/g;
const LATIN_MARKS = /[̀-ͯ]/g;

/**
 * One spelling of a word, so that what a person types finds what a supplier typed.
 *
 * <p>Arabic is written with the vowels left out and the hamza put on or left off as the
 * writer feels: سقالة, سقاله and سُقالة are the same word to everyone except a string
 * comparison. A cashier typing سقاله and getting nothing concludes the product is missing.
 * The same fold takes échafaudage to echafaudage and ٠١٢ to 012, so a barcode read off a
 * label in Arabic digits still matches the one in the catalogue.</p>
 */
export function fold(value: string): string {
  return value
    .normalize('NFD')
    .replace(LATIN_MARKS, '')
    .replace(ARABIC_MARKS, '')
    .replace(/[أإآٱ]/g, 'ا') // أ إ آ ٱ → ا
    .replace(/ة/g, 'ه') // ة → ه
    .replace(/[ىی]/g, 'ي') // ى ی → ي
    .replace(/[٠-٩]/g, (d) => String(d.charCodeAt(0) - 0x0660))
    .replace(/[۰-۹]/g, (d) => String(d.charCodeAt(0) - 0x06f0))
    .toLowerCase()
    .trim();
}

/**
 * A code as a scanner would have to read it twice to be the same code: no spaces, no
 * hyphens, one case.
 */
function normalizeCode(value: string): string {
  return fold(value).replace(/[\s\-_.]/g, '');
}

/**
 * Whether two codes name the same article.
 *
 * <p>The leading-zero comparison is not pedantry: a twelve-digit UPC printed in the United
 * States is the same article as the thirteen-digit EAN in the catalogue with a zero in front,
 * and which of the two a given scanner sends depends on the scanner. Without this the same
 * box scans differently at two tills in the same shop.</p>
 */
export function sameCode(a: string | null | undefined, b: string | null | undefined): boolean {
  const left = normalizeCode(a ?? '');
  const right = normalizeCode(b ?? '');
  if (!left || !right) return false;
  if (left === right) return true;

  const bare = (v: string) => (/^\d+$/.test(v) ? v.replace(/^0+/, '') || '0' : v);
  return bare(left) === bare(right);
}

/**
 * Every product whose barcode or SKU *is* the scanned code.
 *
 * <p>All of them, not the first: two products sharing a barcode is a data error somebody has
 * to see, and a picker that silently took the first one would put the wrong article on an
 * invoice every time and never say so. The caller adds the code when there is one match and
 * shows the choice when there are more.</p>
 */
export function matchCode<T extends SearchableProduct>(products: readonly T[], code: string): T[] {
  const term = code.trim();
  if (!term) return [];

  const byBarcode = products.filter((p) => sameCode(p.barcode, term));
  if (byBarcode.length) return byBarcode;
  return products.filter((p) => sameCode(p.sku, term));
}

/**
 * The parts of a code somebody might type: the whole of it, and each piece between the
 * separators. SCA-2M is reached by "sca" and by "2m", and neither is a special case.
 */
function codeParts(value: string): string[] {
  const whole = normalizeCode(value);
  if (!whole) return [];
  const pieces = fold(value)
    .split(/[\s\-_./]+/)
    .filter((piece) => piece && piece !== whole);
  return [whole, ...pieces];
}

/** Lower is better. The order a person expects: what they scanned, then what they typed. */
const enum Rank {
  CodeExact = 0,
  CodePartExact = 1,
  CodePrefix = 2,
  NamePrefix = 3,
  WordStart = 4,
  Anywhere = 5,
  NoMatch = 99,
}

function rankOne(haystacks: readonly string[], codes: readonly string[][], word: string): Rank {
  for (const parts of codes) {
    if (parts[0] === word) return Rank.CodeExact;
  }
  for (const parts of codes) {
    if (parts.includes(word)) return Rank.CodePartExact;
  }
  // Codes are matched from the front and never in the middle. A barcode holds every digit
  // somewhere, so "3" matching inside one would make a second word widen the list instead of
  // narrowing it — and narrowing is the only reason anybody types a second word.
  for (const parts of codes) {
    if (parts.some((part) => part.startsWith(word))) return Rank.CodePrefix;
  }
  for (const name of haystacks) {
    if (name.startsWith(word)) return Rank.NamePrefix;
  }
  for (const name of haystacks) {
    const at = name.indexOf(word);
    if (at < 0) continue;
    return at > 0 && /[\s\-/(]/.test(name[at - 1]) ? Rank.WordStart : Rank.Anywhere;
  }
  return Rank.NoMatch;
}

/**
 * The catalogue narrowed to what the typed text can mean, best guess first.
 *
 * <p>Words are required separately, so "ech 2m" finds an échafaudage 2 m without anyone
 * having to remember whether the catalogue writes it "2m", "2 m" or "2 mètres". Requiring
 * every word is what keeps a second word useful: it always narrows, never widens.</p>
 *
 * @param limit how many to return. A list longer than a screenful is not a list a person
 *   reads, it is one they scroll past — and rendering the whole catalogue on each keystroke
 *   is what makes a search box feel slow on a counter terminal.
 */
export function searchProducts<T extends SearchableProduct>(
  products: readonly T[],
  term: string,
  limit = 12,
): T[] {
  const words = fold(term).split(/\s+/).filter(Boolean);
  if (!words.length) return products.slice(0, limit);

  const scored: { product: T; rank: number; order: number }[] = [];
  products.forEach((product, order) => {
    const names = [product.nameAr, product.nameFr, product.nameEn].filter(Boolean).map(fold);
    const codes = [product.sku, product.barcode ?? ''].filter(Boolean).map(codeParts);

    let worst = Rank.CodeExact;
    for (const word of words) {
      const rank = rankOne(names, codes, word);
      if (rank === Rank.NoMatch) return;
      if (rank > worst) worst = rank;
    }
    scored.push({ product, rank: worst, order });
  });

  // Catalogue order within a rank, so the list does not reshuffle itself as somebody types
  // one more letter that changes nothing.
  scored.sort((a, b) => a.rank - b.rank || a.order - b.order);
  return scored.slice(0, limit).map((s) => s.product);
}

/**
 * What a picker should do with what is in its box: take one product, or show what it found.
 *
 * <p>Keeping the decision here rather than in the component is what makes "scan and it is on
 * the line" identical in the till and in a rental contract. The single-match rule matters as
 * much as the code rule — someone who types enough to leave one product standing means that
 * product, and making them then reach for the mouse is the thing they complain about.</p>
 */
export function resolveEntry<T extends SearchableProduct>(
  products: readonly T[],
  term: string,
): { taken: T | null; shown: T[]; exact: boolean } {
  const typed = term.trim();
  if (!typed) return { taken: null, shown: [], exact: false };

  const exact = matchCode(products, typed);
  if (exact.length === 1) return { taken: exact[0], shown: exact, exact: true };
  if (exact.length > 1) return { taken: null, shown: exact, exact: true };

  const found = searchProducts(products, typed);
  return { taken: found.length === 1 ? found[0] : null, shown: found, exact: false };
}

/**
 * Whether what is in the box reads as a code a scanner would type — four digits or more, in
 * either numeral set — rather than a name. Enter takes the top of the list for a name; for a code
 * that matched nothing it must not, or an unknown barcode becomes its nearest neighbour.
 */
export function looksLikeCode(term: string): boolean {
  return /^[0-9\u0660-\u0669]{4,}$/.test(term.trim());
}
