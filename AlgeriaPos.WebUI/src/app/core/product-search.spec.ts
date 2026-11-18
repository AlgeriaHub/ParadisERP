import { fold, matchCode, resolveEntry, sameCode, searchProducts, SearchableProduct, looksLikeCode } from './product-search';

const product = (over: Partial<SearchableProduct> & { id: string }): SearchableProduct => ({
  sku: over.id.toUpperCase(),
  barcode: null,
  nameAr: '',
  nameFr: '',
  nameEn: '',
  ...over,
});

/** The catalogue of a scaffolding hire, which is what this has to work on. */
const catalogue: SearchableProduct[] = [
  product({
    id: 'p1',
    sku: 'SCA-2M',
    barcode: '6130001234567',
    nameAr: 'سِقالة 2 متر',
    nameFr: 'Échafaudage 2 m',
    nameEn: 'Scaffold 2 m',
  }),
  product({
    id: 'p2',
    sku: 'SCA-3M',
    barcode: '6130001234574',
    nameAr: 'سقالة 3 متر',
    nameFr: 'Échafaudage 3 m',
    nameEn: 'Scaffold 3 m',
  }),
  product({
    id: 'p3',
    sku: 'CLA-90',
    barcode: '012345678905',
    nameAr: 'كلابة 90 درجة',
    nameFr: 'Collier 90°',
    nameEn: 'Clamp 90°',
  }),
  product({
    id: 'p4',
    sku: 'PLN-40',
    barcode: null,
    nameAr: 'لوح خشب 40',
    nameFr: 'Planche 40',
    nameEn: 'Plank 40',
  }),
];

describe('fold', () => {
  it('spells an Arabic word one way however it was written', () => {
    // The three spellings a supplier, a cashier and a label will each use for one word.
    expect(fold('سِقَالَة')).toBe(fold('سقاله'));
    expect(fold('إيجار')).toBe(fold('ايجار'));
    expect(fold('مصطفى')).toBe(fold('مصطفي'));
  });

  it('spells a French word one way whether or not the accent was typed', () => {
    expect(fold('Échafaudage')).toBe('echafaudage');
    expect(fold('unité')).toBe('unite');
  });

  it('reads a number in Arabic digits as a number', () => {
    expect(fold('٦١٣٠٠٠١')).toBe('6130001');
  });
});

describe('sameCode', () => {
  it('ignores how the code was spaced', () => {
    expect(sameCode('6130 0012 34567', '6130001234567')).toBeTrue();
    expect(sameCode('sca-2m', 'SCA-2M')).toBeTrue();
  });

  it('reads a 12-digit UPC as the 13-digit EAN it is', () => {
    // Which of the two a scanner sends is a scanner setting, and the shop owns one of each.
    expect(sameCode('012345678905', '12345678905')).toBeTrue();
  });

  it('does not treat two different codes as one', () => {
    expect(sameCode('6130001234567', '6130001234574')).toBeFalse();
    expect(sameCode('', '6130001234567')).toBeFalse();
    expect(sameCode(null, null)).toBeFalse();
  });

  it('leaves a code that is only zeros alone rather than emptying it', () => {
    expect(sameCode('000', '0')).toBeTrue();
  });
});

describe('matchCode', () => {
  it('finds the article by its barcode', () => {
    expect(matchCode(catalogue, '6130001234574').map((p) => p.id)).toEqual(['p2']);
  });

  it('finds it by its SKU when nothing carries that barcode', () => {
    expect(matchCode(catalogue, 'pln-40').map((p) => p.id)).toEqual(['p4']);
  });

  it('returns both when two products carry the same barcode', () => {
    // A data error, and one somebody has to be shown. Taking the first would put the wrong
    // article on the invoice and say nothing.
    const clashing = [...catalogue, product({ id: 'p5', barcode: '6130001234567' })];
    expect(matchCode(clashing, '6130001234567').map((p) => p.id)).toEqual(['p1', 'p5']);
  });

  it('prefers a barcode over a SKU that happens to read the same', () => {
    const odd = [product({ id: 'p9', sku: '6130001234567' }), ...catalogue];
    expect(matchCode(odd, '6130001234567').map((p) => p.id)).toEqual(['p1']);
  });
});

describe('searchProducts', () => {
  it('finds an Arabic name typed without its vowels', () => {
    expect(searchProducts(catalogue, 'سقاله').map((p) => p.id)).toEqual(['p1', 'p2']);
  });

  it('finds a French name typed without its accent', () => {
    expect(searchProducts(catalogue, 'echafaudage').map((p) => p.id)).toEqual(['p1', 'p2']);
  });

  it('narrows on the second word wherever that word sits', () => {
    expect(searchProducts(catalogue, 'echa 3').map((p) => p.id)).toEqual(['p2']);
    expect(searchProducts(catalogue, '3 echa').map((p) => p.id)).toEqual(['p2']);
  });

  it('puts the code that was typed in full above the names that merely contain it', () => {
    const many = [
      product({ id: 'a', sku: 'X-100', nameFr: 'Serrage 100' }),
      product({ id: 'b', sku: '100', nameFr: 'Tube' }),
    ];
    expect(searchProducts(many, '100').map((p) => p.id)).toEqual(['b', 'a']);
  });

  it('keeps catalogue order among equally good matches, so the list does not jump', () => {
    expect(searchProducts(catalogue, 'sca').map((p) => p.id)).toEqual(['p1', 'p2']);
  });

  it('returns nothing rather than everything when nothing matches', () => {
    expect(searchProducts(catalogue, 'zzz')).toEqual([]);
  });

  it('shows the head of the catalogue when nothing has been typed', () => {
    expect(searchProducts(catalogue, '   ', 2).map((p) => p.id)).toEqual(['p1', 'p2']);
  });

  it('stops at the limit', () => {
    expect(searchProducts(catalogue, 'sca', 1).map((p) => p.id)).toEqual(['p1']);
  });
});

describe('resolveEntry', () => {
  it('takes the article straight off a scan', () => {
    const { taken } = resolveEntry(catalogue, '6130001234567');
    expect(taken?.id).toBe('p1');
  });

  it('takes it when what was typed leaves one product standing', () => {
    // Somebody who typed this far means this product; making them reach for the mouse now is
    // the part that gets complained about.
    expect(resolveEntry(catalogue, 'planche').taken?.id).toBe('p4');
  });

  it('takes nothing and shows the choice when two could be meant', () => {
    const { taken, shown } = resolveEntry(catalogue, 'echafaudage');
    expect(taken).toBeNull();
    expect(shown.map((p) => p.id)).toEqual(['p1', 'p2']);
  });

  it('takes nothing on an empty box', () => {
    expect(resolveEntry(catalogue, '  ')).toEqual({ taken: null, shown: [], exact: false });
  });

  it('shows the clash rather than guessing when one barcode is on two products', () => {
    const clashing = [...catalogue, product({ id: 'p5', barcode: '6130001234567' })];
    const { taken, shown } = resolveEntry(clashing, '6130001234567');
    expect(taken).toBeNull();
    expect(shown.map((p) => p.id)).toEqual(['p1', 'p5']);
  });
});

describe('looksLikeCode', () => {
  it('reads four digits or more, in either numeral set, as a code a scanner typed', () => {
    expect(looksLikeCode('6130000000001')).toBeTrue();
    expect(looksLikeCode(' ٦١٣٠٠٠ ')).toBeTrue();
  });
  it('reads a name, a short number or a reference with letters as something a person typed', () => {
    expect(looksLikeCode('ART')).toBeFalse();
    expect(looksLikeCode('ارجل')).toBeFalse();
    expect(looksLikeCode('12')).toBeFalse();
    expect(looksLikeCode('ART-0001')).toBeFalse();
  });
});

describe('resolveEntry — exact', () => {
  const items = [
    { id: 'a', sku: 'ART-0001', barcode: '6130000000001', nameAr: 'ارجل جالفا', nameFr: 'Pieds galva', nameEn: 'Galva legs' },
    { id: 'b', sku: 'ART-0003', barcode: '6130000000003', nameAr: 'اس كروشي', nameFr: 'Collier', nameEn: 'Clamp' },
  ];
  it('says when the entry was a code read exactly, so a scan keeps the focus', () => {
    expect(resolveEntry(items, '6130000000001')).toEqual(jasmine.objectContaining({ exact: true }));
    expect(resolveEntry(items, 'galva').exact).toBeFalse();
  });
});
