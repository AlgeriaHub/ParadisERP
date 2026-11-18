import { ApiError } from '../../../core/api.service';
import { matchCode, searchProducts } from '../../../core/product-search';
import { Product, soldAtTill } from '../../../core/models';
import { MANUFACTURING_TRANSLATIONS } from '../../../core/i18n/translations-manufacturing';
import { MFG, mfgAny, mfgCan, mfgError, qty } from './manufacturing-shared';

describe('manufacturing, shared', () => {
  const t = (key: string) => MANUFACTURING_TRANSLATIONS.ar[key] ?? key;
  const problem = (status: number, code: string, detail = 'English sentence from the server.') =>
    new ApiError(JSON.stringify({ status, detail, code }), status);

  it('reads the permissions from the capabilities the login answered, never from the role', () => {
    expect(mfgCan({ capabilities: [MFG.view] }, MFG.view)).toBeTrue();
    expect(mfgCan({ capabilities: [MFG.view] }, MFG.bom)).toBeFalse();
    // A session stored before capabilities existed holds none.
    expect(mfgCan({}, MFG.view)).toBeFalse();
    expect(mfgAny({ capabilities: [MFG.settings] })).toBeTrue();
    expect(mfgAny(null)).toBeFalse();
  });

  it('says a refusal in the reader\'s language, not in the server\'s English', () => {
    expect(mfgError(problem(409, 'bom_frozen'), t)).toBe(MANUFACTURING_TRANSLATIONS.ar['mfg.error.bom_frozen']);
    expect(mfgError(problem(400, 'no_conversion'), t)).not.toContain('English');
    // An unknown code falls back to the server's sentence rather than to nothing.
    expect(mfgError(problem(400, 'something_new', 'A new rule.'), t)).toBe('A new rule.');
  });

  it('shows quantities without float noise', () => {
    expect(qty(0.1 + 0.2)).toBe('0.3');
    expect(qty(53.55)).toBe('53.55');
    expect(qty(1338.75)).toBe('1338.75');
    expect(qty(null)).toBe('—');
  });

  describe('the till', () => {
    const product = (over: Partial<Product>): Product =>
      ({
        id: over.id ?? 'p',
        sku: over.sku ?? 'SKU',
        barcode: over.barcode ?? null,
        nameAr: '',
        nameFr: over.nameFr ?? 'Produit',
        nameEn: '',
        categoryId: null,
        price: 100,
        taxRate: 19,
        unit: 'pcs',
        trackStock: true,
        stockQty: 5,
        reorderLevel: 0,
        isActive: true,
        version: 1,
        isDeleted: false,
        ...over,
      }) as Product;

    const nails = product({ id: 'nails', sku: 'NAIL-70', barcode: '6130000000017', nameFr: 'Pointes 70' });
    const wire = product({ id: 'wire', sku: 'WIRE-3MM', barcode: '6130000000024', nameFr: 'Fil de fer', hiddenAtTill: true });
    // Cached before the flag existed: no field at all, which reads as shown until the next pull.
    const legacy = product({ id: 'old', sku: 'OLD-1', nameFr: 'Ancien' });
    delete (legacy as Partial<Product>).hiddenAtTill;
    const sale = [nails, wire, legacy].filter(soldAtTill);

    it('keeps the workshop\'s materials out of the grid, the search and the scanner alike', () => {
      expect(sale.map((p) => p.id)).toEqual(['nails', 'old']);
      expect(searchProducts(sale, 'fil', 50)).toEqual([]);
      expect(matchCode(sale, '6130000000024')).toEqual([]);
      expect(matchCode(sale, '6130000000017').map((p) => p.id)).toEqual(['nails']);
    });
  });
});
